using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

/// <summary>
/// Owns the same final provider capability after Core has durably claimed the exact
/// Pending -> MutationStarted entry barrier. This scope authorizes only a later separately
/// reviewed same-lease mutation primitive; it does not expose or perform that mutation.
/// </summary>
public sealed class FileDeleteOperationMutationBarrierScope : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private IFileDeleteOperationFinalMutationLease? _lease;

    internal FileDeleteOperationMutationBarrierScope(
        FileDeleteOperationHistoryBindingEvidence priorBindingEvidence,
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence,
        FileDeleteOperationActionHistory barrierHistory,
        IFileDeleteOperationFinalMutationLease lease)
    {
        ArgumentNullException.ThrowIfNull(priorBindingEvidence);
        ArgumentNullException.ThrowIfNull(finalEvidence);
        ArgumentNullException.ThrowIfNull(barrierHistory);
        ArgumentNullException.ThrowIfNull(lease);

        PriorBindingEvidence = priorBindingEvidence;
        FinalEvidence = finalEvidence;
        BarrierHistory = barrierHistory;
        _lease = lease;
    }

    public FileDeleteOperationHistoryBindingEvidence PriorBindingEvidence { get; }

    public FileDeleteOperationFinalMutationLeaseEvidence FinalEvidence { get; }

    public FileDeleteOperationActionHistory BarrierHistory { get; }

    public FileDeleteOperationUserAuthorizationReceipt Authorization => PriorBindingEvidence.Authorization;

    public int Ordinal => PriorBindingEvidence.Ordinal;

    public bool FinalLeaseHeld => Volatile.Read(ref _lease) is not null;

    public bool DeleteAccessCapabilityHeld
    {
        get
        {
            var lease = Volatile.Read(ref _lease);
            return lease is not null && lease.DeleteAccessCapabilityHeld;
        }
    }

    public bool MutationBarrierSatisfied => true;

    public bool DeleteMutationAuthorized
    {
        get
        {
            var lease = Volatile.Read(ref _lease);
            return lease is not null &&
                lease.DeleteAccessCapabilityHeld &&
                !lease.DeleteMutationAuthorized &&
                ReferenceEquals(lease.Evidence, FinalEvidence);
        }
    }

    public bool DeleteMutationPerformed => false;

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lease = Volatile.Read(ref _lease);
            if (lease is null)
            {
                return;
            }

            await lease.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _lease, null);
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}

/// <summary>
/// Transfers ownership of the exact accepted final lease and claims the durable per-entry
/// mutation barrier while that capability remains privately held. Cancellation is honored only
/// before ownership transfer; the short durable barrier/inspection/recovery section is deliberately
/// non-cancellable so caller cancellation cannot manufacture an avoidable commit-outcome ambiguity.
/// </summary>
public static class FileDeleteOperationMutationBarrier
{
    private const string ValidationFailureCode = "DeleteMutationBarrierValidationFailed";
    private const string AmbiguousBarrierCode = "DeleteMutationBarrierOutcomeAmbiguous";

    public static async ValueTask<FileDeleteOperationMutationBarrierScope> ClaimAsync(
        FileDeleteOperationFinalMutationLeaseScope finalLeaseScope,
        IFileDeleteOperationActionHistoryStore historyStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalLeaseScope);
        ArgumentNullException.ThrowIfNull(historyStore);
        cancellationToken.ThrowIfCancellationRequested();

        if (finalLeaseScope.DeleteMutationAuthorized ||
            finalLeaseScope.MutationBarrierSatisfied ||
            !finalLeaseScope.PriorReadOnlyLeaseReleaseObserved ||
            !finalLeaseScope.FinalLeaseProviderAcquisitionObserved ||
            !finalLeaseScope.FinalLeaseHeld ||
            !finalLeaseScope.DeleteAccessCapabilityHeld)
        {
            throw new InvalidOperationException(
                "Delete mutation barrier requires one live accepted final lease scope that remains before the durable barrier.");
        }

        var priorBinding = finalLeaseScope.PriorBindingEvidence;
        var finalEvidence = finalLeaseScope.FinalEvidence;
        ValidateFinalEvidence(priorBinding, finalEvidence);
        cancellationToken.ThrowIfCancellationRequested();

        // Cancellation stops here. Detach and every durability/recovery operation below use a
        // non-cancellable critical section so a caller cannot interrupt ownership settlement.
        var lease = await finalLeaseScope.DetachLeaseAsync().ConfigureAwait(false);
        var operationId = priorBinding.HistorySnapshot.OperationId;
        var ordinal = priorBinding.Ordinal;

        FileDeleteOperationActionHistory barrierHistory;
        try
        {
            barrierHistory = await historyStore
                .MarkMutationStartedAsync(operationId, ordinal, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception barrierException)
        {
            var resolved = await ResolveBarrierWriteFailureAsync(
                    priorBinding,
                    finalEvidence,
                    historyStore,
                    lease,
                    barrierException)
                .ConfigureAwait(false);
            throw resolved;
        }

        try
        {
            ValidateBarrierHistory(priorBinding, finalEvidence, barrierHistory);
            ValidateLiveLease(finalEvidence, lease);
            return new FileDeleteOperationMutationBarrierScope(
                priorBinding,
                finalEvidence,
                barrierHistory,
                lease);
        }
        catch (Exception validationException)
        {
            var resolved = await RecoverAndReleaseAsync(
                    priorBinding,
                    historyStore,
                    lease,
                    validationException,
                    ValidationFailureCode,
                    "The durable delete mutation barrier was written but its returned evidence could not be trusted.")
                .ConfigureAwait(false);
            throw resolved;
        }
    }

    private static async ValueTask<Exception> ResolveBarrierWriteFailureAsync(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence,
        IFileDeleteOperationActionHistoryStore historyStore,
        IFileDeleteOperationFinalMutationLease lease,
        Exception barrierException)
    {
        FileDeleteOperationActionHistory? observed;
        try
        {
            observed = await historyStore
                .GetAsync(priorBinding.HistorySnapshot.OperationId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception inspectionException)
        {
            return await ReleaseWithFailuresAsync(
                    lease,
                    "Delete mutation barrier write failed and its durable outcome could not be inspected.",
                    barrierException,
                    inspectionException)
                .ConfigureAwait(false);
        }

        if (observed is not null && IsExactPendingHistory(priorBinding, observed))
        {
            return await ReleaseWithFailuresAsync(
                    lease,
                    "Delete mutation barrier write failed before a durable transition was observed.",
                    barrierException)
                .ConfigureAwait(false);
        }

        if (observed is not null && TryValidateBarrierHistory(
                priorBinding,
                finalEvidence,
                observed,
                out _))
        {
            var ambiguous = new InvalidOperationException(
                "Delete mutation barrier write threw after MutationStarted became durable; recovery is required before any later attempt.",
                barrierException);
            return await RecoverAndReleaseAsync(
                    priorBinding,
                    historyStore,
                    lease,
                    ambiguous,
                    AmbiguousBarrierCode,
                    "MutationStarted was observed after an ambiguous delete mutation barrier write.")
                .ConfigureAwait(false);
        }

        Exception ambiguity;
        if (observed is null)
        {
            ambiguity = new InvalidOperationException(
                "Delete mutation barrier write failed and the operation history disappeared during outcome inspection.",
                barrierException);
        }
        else
        {
            TryValidateBarrierHistory(
                priorBinding,
                finalEvidence,
                observed,
                out var validationException);
            ambiguity = validationException is null
                ? new InvalidOperationException(
                    "Delete mutation barrier write failed with an unrecognized durable history state.",
                    barrierException)
                : new AggregateException(
                    "Delete mutation barrier write failed and the observed durable history could not prove either Pending or MutationStarted.",
                    barrierException,
                    validationException);
        }

        return await ReleaseWithFailuresAsync(
                lease,
                "Delete mutation barrier outcome remains ambiguous; no filesystem mutation was attempted.",
                ambiguity)
            .ConfigureAwait(false);
    }

    private static async ValueTask<Exception> RecoverAndReleaseAsync(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        IFileDeleteOperationActionHistoryStore historyStore,
        IFileDeleteOperationFinalMutationLease lease,
        Exception cause,
        string failureCode,
        string failureMessage)
    {
        var failures = new List<Exception> { cause };
        var failure = new FileOperationFailure(
            failureCode,
            failureMessage,
            priorBinding.AuthorizedItem.CanonicalPath,
            Retryable: false);

        try
        {
            var recoveryHistory = await historyStore
                .MarkMutationRecoveryRequiredAsync(
                    priorBinding.HistorySnapshot.OperationId,
                    priorBinding.Ordinal,
                    failure,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ValidateRecoveryHistory(priorBinding, recoveryHistory, failureCode);
        }
        catch (Exception recoveryException)
        {
            failures.Add(recoveryException);
        }

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposalException)
        {
            failures.Add(disposalException);
            return new FileDeleteOperationFinalLeaseReleaseException(
                "Delete mutation barrier recovery failed to release the detached final capability; cleanup ownership is retained for retry.",
                failures,
                lease);
        }

        return failures.Count == 1
            ? failures[0]
            : new AggregateException(
                "Delete mutation barrier recovery encountered one or more failures; durable history remains the restart-time signal.",
                failures);
    }

    private static async ValueTask<Exception> ReleaseWithFailuresAsync(
        IFileDeleteOperationFinalMutationLease lease,
        string message,
        params Exception[] causes)
    {
        var failures = new List<Exception>(causes);
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposalException)
        {
            failures.Add(disposalException);
            return new FileDeleteOperationFinalLeaseReleaseException(
                message + " Final capability release failed; cleanup ownership is retained for retry.",
                failures,
                lease);
        }

        return failures.Count == 1
            ? failures[0]
            : new AggregateException(message, failures);
    }

    private static void ValidateFinalEvidence(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence)
    {
        var authorization = priorBinding.Authorization;
        var ordinal = priorBinding.Ordinal;
        if (finalEvidence.DeleteMutationAuthorized ||
            !finalEvidence.IsBoundTo(authorization, ordinal) ||
            !PathEquals(finalEvidence.CanonicalSourceDirectoryPath, authorization.CanonicalSourceDirectoryPath) ||
            finalEvidence.SourceDirectoryIdentity != authorization.SourceDirectoryIdentity ||
            !PathEquals(finalEvidence.CanonicalSourcePath, priorBinding.AuthorizedItem.CanonicalPath) ||
            finalEvidence.SourceIdentity != priorBinding.AuthorizedItem.Identity)
        {
            throw new InvalidOperationException(
                "Delete mutation barrier requires final lease evidence bound to the exact authorization/root/file identity.");
        }
    }

    private static void ValidateLiveLease(
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence,
        IFileDeleteOperationFinalMutationLease lease)
    {
        if (lease.DeleteMutationAuthorized ||
            !lease.DeleteAccessCapabilityHeld ||
            !ReferenceEquals(lease.Evidence, finalEvidence))
        {
            throw new InvalidOperationException(
                "Delete mutation barrier requires the exact transferred final capability to remain live after the durable transition.");
        }
    }

    private static bool TryValidateBarrierHistory(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence,
        FileDeleteOperationActionHistory history,
        out Exception? validationException)
    {
        try
        {
            ValidateBarrierHistory(priorBinding, finalEvidence, history);
            validationException = null;
            return true;
        }
        catch (Exception exception)
        {
            validationException = exception;
            return false;
        }
    }

    private static void ValidateBarrierHistory(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence,
        FileDeleteOperationActionHistory history)
    {
        ValidateStaticHistoryProvenance(priorBinding, history);
        var prior = priorBinding.HistorySnapshot;
        var ordinal = priorBinding.Ordinal;

        for (var index = 0; index < history.Entries.Count; index++)
        {
            var current = history.Entries[index];
            var previous = prior.Entries[index];
            if (index != ordinal)
            {
                if (current != previous)
                {
                    throw new InvalidOperationException(
                        "Delete mutation barrier changed history evidence for an entry other than the selected ordinal.");
                }
                continue;
            }

            if (current.Ordinal != previous.Ordinal ||
                current.Entry != previous.Entry ||
                !PathEquals(current.CanonicalSourcePath, previous.CanonicalSourcePath) ||
                current.SourceIdentity != previous.SourceIdentity ||
                current.State != FileDeleteOperationActionEntryState.MutationStarted ||
                !current.MutationStartedAtUtc.HasValue ||
                current.CompletedAtUtc.HasValue ||
                current.Failure is not null ||
                !PathEquals(current.CanonicalSourcePath, finalEvidence.CanonicalSourcePath) ||
                current.SourceIdentity != finalEvidence.SourceIdentity)
            {
                throw new InvalidOperationException(
                    "Delete mutation barrier history does not contain the exact selected MutationStarted entry and unchanged provenance.");
            }
        }
    }

    private static void ValidateRecoveryHistory(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        FileDeleteOperationActionHistory history,
        string expectedFailureCode)
    {
        ValidateStaticHistoryProvenance(priorBinding, history);
        var prior = priorBinding.HistorySnapshot;
        var ordinal = priorBinding.Ordinal;

        for (var index = 0; index < history.Entries.Count; index++)
        {
            var current = history.Entries[index];
            var previous = prior.Entries[index];
            if (index != ordinal)
            {
                if (current != previous)
                {
                    throw new InvalidOperationException(
                        "Delete mutation recovery changed history evidence for an unrelated entry.");
                }
                continue;
            }

            if (current.Ordinal != previous.Ordinal ||
                current.Entry != previous.Entry ||
                !PathEquals(current.CanonicalSourcePath, previous.CanonicalSourcePath) ||
                current.SourceIdentity != previous.SourceIdentity ||
                current.State != FileDeleteOperationActionEntryState.RecoveryRequired ||
                !current.MutationStartedAtUtc.HasValue ||
                !current.CompletedAtUtc.HasValue ||
                current.Failure is null ||
                !string.Equals(current.Failure.Code, expectedFailureCode, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Delete mutation recovery history does not retain the exact selected recovery-sensitive entry.");
            }
        }
    }

    private static void ValidateStaticHistoryProvenance(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        FileDeleteOperationActionHistory history)
    {
        var prior = priorBinding.HistorySnapshot;
        if (history.OperationId != prior.OperationId ||
            history.AuthorizationId != prior.AuthorizationId ||
            history.QueuedAtUtc != prior.QueuedAtUtc ||
            history.ValidatedAtUtc != prior.ValidatedAtUtc ||
            history.AuthorizedAtUtc != prior.AuthorizedAtUtc ||
            history.StartedAtUtc != prior.StartedAtUtc ||
            history.CompletedAtUtc.HasValue ||
            history.TerminalState.HasValue ||
            !string.Equals(history.SourcePaneId, prior.SourcePaneId, StringComparison.Ordinal) ||
            history.SourceTabId != prior.SourceTabId ||
            !PathEquals(history.CanonicalSourceDirectoryPath, prior.CanonicalSourceDirectoryPath) ||
            history.SourceDirectoryIdentity != prior.SourceDirectoryIdentity ||
            history.Entries.Count != prior.Entries.Count)
        {
            throw new InvalidOperationException(
                "Delete mutation barrier history changed operation-level authorization/provenance evidence.");
        }
    }

    private static bool IsExactPendingHistory(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        FileDeleteOperationActionHistory history)
    {
        try
        {
            ValidateStaticHistoryProvenance(priorBinding, history);
        }
        catch
        {
            return false;
        }

        var prior = priorBinding.HistorySnapshot;
        for (var index = 0; index < prior.Entries.Count; index++)
        {
            if (history.Entries[index] != prior.Entries[index])
            {
                return false;
            }
        }
        return true;
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
