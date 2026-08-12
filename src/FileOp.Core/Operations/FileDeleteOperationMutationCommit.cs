using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// Core-minted, one-operation authorization for a destructive same-lease file mutation.
/// Construction is internal and requires a live mutation-barrier scope; value evidence alone
/// cannot manufacture this authority.
/// </summary>
public sealed class FileDeleteOperationMutationAuthorization
{
    internal FileDeleteOperationMutationAuthorization(
        FileDeleteOperationMutationBarrierScope barrierScope)
    {
        ArgumentNullException.ThrowIfNull(barrierScope);
        if (!barrierScope.MutationBarrierSatisfied ||
            !barrierScope.DeleteMutationAuthorized ||
            !barrierScope.FinalLeaseHeld ||
            !barrierScope.DeleteAccessCapabilityHeld)
        {
            throw new InvalidOperationException(
                "Delete mutation authorization requires one live exact mutation-barrier scope.");
        }

        FinalEvidence = barrierScope.FinalEvidence;
        BarrierHistory = barrierScope.BarrierHistory;
    }

    public FileDeleteOperationFinalMutationLeaseEvidence FinalEvidence { get; }

    public FileDeleteOperationActionHistory BarrierHistory { get; }

    public FileDeleteOperationUserAuthorizationReceipt Authorization => FinalEvidence.Authorization;

    public int Ordinal => FinalEvidence.Ordinal;

    public string CanonicalSourcePath => FinalEvidence.CanonicalSourcePath;

    public FileIdentity SourceIdentity => FinalEvidence.SourceIdentity;

    public bool MutationBarrierSatisfied => true;

    public bool DeleteMutationAuthorized => true;

    public bool DeleteMutationPerformed => false;

    public bool IsBoundTo(FileDeleteOperationFinalMutationLeaseEvidence evidence) =>
        ReferenceEquals(FinalEvidence, evidence) &&
        ReferenceEquals(Authorization, evidence.Authorization) &&
        Ordinal == evidence.Ordinal &&
        SourceIdentity == evidence.SourceIdentity &&
        string.Equals(
            CanonicalSourcePath,
            evidence.CanonicalSourcePath,
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Optional destructive facet of a provider-owned final lease. The underlying operating-system
/// handle remains private; Core can invoke this facet only after it has claimed the durable
/// mutation barrier and minted the exact authorization above.
/// </summary>
public interface IFileDeleteOperationSameLeaseMutation
{
    ValueTask MarkDeletePendingAsync(
        FileDeleteOperationMutationAuthorization authorization,
        CancellationToken cancellationToken = default);
}

internal readonly record struct FileDeleteOperationMutationLeaseTransfer(
    IFileDeleteOperationFinalMutationLease Lease,
    IFileDeleteOperationSameLeaseMutation Mutation,
    FileDeleteOperationMutationAuthorization Authorization);

/// <summary>
/// Value-only evidence that the same-lease mutation completed, the final lease was released,
/// and the exact durable action-history entry was observed as Committed. It carries no live
/// mutation authority.
/// </summary>
public sealed class FileDeleteOperationMutationCommitResult
{
    internal FileDeleteOperationMutationCommitResult(
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence,
        FileDeleteOperationActionHistory committedHistory)
    {
        ArgumentNullException.ThrowIfNull(finalEvidence);
        ArgumentNullException.ThrowIfNull(committedHistory);
        FinalEvidence = finalEvidence;
        CommittedHistory = committedHistory;
    }

    public FileDeleteOperationFinalMutationLeaseEvidence FinalEvidence { get; }

    public FileDeleteOperationActionHistory CommittedHistory { get; }

    public FileDeleteOperationUserAuthorizationReceipt Authorization => FinalEvidence.Authorization;

    public int Ordinal => FinalEvidence.Ordinal;

    public FileIdentity DeletedSourceIdentity => FinalEvidence.SourceIdentity;

    public bool MutationBarrierSatisfied => true;

    public bool DeleteMutationPerformed => true;

    public bool DurableCommitObserved => true;

    public bool DeleteMutationAuthorized => false;
}

/// <summary>
/// Consumes one live mutation-barrier scope, invokes the destructive facet on the exact same
/// provider lease, releases that lease so the provider can complete delete-on-close semantics,
/// then settles the identity-bound durable history entry. Cancellation is honored only before
/// destructive ownership transfer. Every step after transfer is deliberately non-cancellable.
/// </summary>
public static class FileDeleteOperationMutationCommit
{
    private const string MutationFailureCode = "DeleteSameLeaseMutationFailed";
    private const string ReleaseFailureCode = "DeleteSameLeaseReleaseFailed";
    private const string CommitFailureCode = "DeleteMutationCommitOutcomeAmbiguous";

    public static async ValueTask<FileDeleteOperationMutationCommitResult> ExecuteAsync(
        FileDeleteOperationMutationBarrierScope barrierScope,
        IFileDeleteOperationActionHistoryStore historyStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(barrierScope);
        ArgumentNullException.ThrowIfNull(historyStore);
        cancellationToken.ThrowIfCancellationRequested();

        if (!barrierScope.MutationBarrierSatisfied ||
            !barrierScope.DeleteMutationAuthorized ||
            !barrierScope.FinalLeaseHeld ||
            !barrierScope.DeleteAccessCapabilityHeld ||
            barrierScope.DeleteMutationPerformed)
        {
            throw new InvalidOperationException(
                "Delete mutation commit requires one live exact barrier scope before any filesystem mutation has occurred.");
        }

        ValidateBarrierInputs(barrierScope);
        cancellationToken.ThrowIfCancellationRequested();

        // Cancellation stops at this ownership transfer. Once the public barrier alias becomes
        // inert, mutation/release/settlement/recovery must run to a durable conclusion or leave
        // explicit recovery evidence.
        var transfer = await barrierScope
            .DetachLeaseForMutationAsync(cancellationToken)
            .ConfigureAwait(false);
        var lease = transfer.Lease;
        var mutation = transfer.Mutation;
        var authorization = transfer.Authorization;

        try
        {
            await mutation
                .MarkDeletePendingAsync(authorization, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception mutationException)
        {
            throw await RecoverAndReleaseAsync(
                    barrierScope,
                    historyStore,
                    lease,
                    mutationException,
                    MutationFailureCode,
                    "The same-lease file disposition call did not complete successfully; recovery is required before any later attempt.")
                .ConfigureAwait(false);
        }

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception releaseException)
        {
            var failures = new List<Exception> { releaseException };
            await TryMarkRecoveryAsync(
                    barrierScope,
                    historyStore,
                    failures,
                    ReleaseFailureCode,
                    "The file was marked for deletion but releasing the exact final lease did not complete; deletion outcome requires recovery.")
                .ConfigureAwait(false);
            throw new FileDeleteOperationFinalLeaseReleaseException(
                "Same-handle delete mutation could not prove final lease release after marking deletion; cleanup ownership is retained for retry.",
                failures,
                lease);
        }

        FileDeleteOperationActionHistory committedHistory;
        try
        {
            committedHistory = await historyStore
                .CommitDeletedAsync(
                    barrierScope.BarrierHistory.OperationId,
                    barrierScope.Ordinal,
                    barrierScope.FinalEvidence.SourceIdentity,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception commitException)
        {
            return await ResolveCommitOutcomeAsync(
                    barrierScope,
                    historyStore,
                    commitException)
                .ConfigureAwait(false);
        }

        try
        {
            ValidateCommittedHistory(barrierScope, committedHistory);
            return new FileDeleteOperationMutationCommitResult(
                barrierScope.FinalEvidence,
                committedHistory);
        }
        catch (Exception validationException)
        {
            return await ResolveCommitOutcomeAsync(
                    barrierScope,
                    historyStore,
                    validationException)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<FileDeleteOperationMutationCommitResult> ResolveCommitOutcomeAsync(
        FileDeleteOperationMutationBarrierScope barrierScope,
        IFileDeleteOperationActionHistoryStore historyStore,
        Exception commitException)
    {
        FileDeleteOperationActionHistory? observed;
        try
        {
            observed = await historyStore
                .GetAsync(barrierScope.BarrierHistory.OperationId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception inspectionException)
        {
            throw new AggregateException(
                "The file mutation completed but durable commit outcome could not be inspected. Existing MutationStarted/Committed history is the restart-time authority.",
                commitException,
                inspectionException);
        }

        if (observed is not null && TryValidateCommittedHistory(barrierScope, observed, out _))
        {
            return new FileDeleteOperationMutationCommitResult(
                barrierScope.FinalEvidence,
                observed);
        }

        if (observed is not null && IsExactBarrierHistory(barrierScope, observed))
        {
            var failures = new List<Exception> { commitException };
            await TryMarkRecoveryAsync(
                    barrierScope,
                    historyStore,
                    failures,
                    CommitFailureCode,
                    "The same-handle file mutation completed but its identity-bound durable commit could not be proven.")
                .ConfigureAwait(false);

            throw failures.Count == 1
                ? new InvalidOperationException(
                    "The file mutation completed but durable commit was not proven; recovery is required.",
                    failures[0])
                : new AggregateException(
                    "The file mutation completed but durable commit/recovery settlement encountered failures.",
                    failures);
        }

        Exception durableStateException;
        if (observed is null)
        {
            durableStateException = new InvalidOperationException(
                "Delete action history disappeared while inspecting a completed filesystem mutation.");
        }
        else if (TryValidateRecoveryHistory(
                barrierScope,
                observed,
                expectedFailureCode: null,
                out _))
        {
            durableStateException = new InvalidOperationException(
                "Delete action history already requires recovery after the filesystem mutation.");
        }
        else
        {
            TryValidateCommittedHistory(barrierScope, observed, out var committedValidation);
            durableStateException = committedValidation ?? new InvalidOperationException(
                "Delete action history is in an unrecognized state after filesystem mutation.");
        }

        throw new AggregateException(
            "The file mutation completed but durable history could not prove Committed or a safely recoverable MutationStarted state.",
            commitException,
            durableStateException);
    }

    private static async ValueTask<Exception> RecoverAndReleaseAsync(
        FileDeleteOperationMutationBarrierScope barrierScope,
        IFileDeleteOperationActionHistoryStore historyStore,
        IFileDeleteOperationFinalMutationLease lease,
        Exception cause,
        string failureCode,
        string failureMessage)
    {
        var failures = new List<Exception> { cause };
        await TryMarkRecoveryAsync(
                barrierScope,
                historyStore,
                failures,
                failureCode,
                failureMessage)
            .ConfigureAwait(false);

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception releaseException)
        {
            failures.Add(releaseException);
            return new FileDeleteOperationFinalLeaseReleaseException(
                "Same-handle delete failure could not release the detached final capability; cleanup ownership is retained for retry.",
                failures,
                lease);
        }

        return failures.Count == 1
            ? failures[0]
            : new AggregateException(
                "Same-handle delete mutation/recovery encountered one or more failures.",
                failures);
    }

    private static async ValueTask TryMarkRecoveryAsync(
        FileDeleteOperationMutationBarrierScope barrierScope,
        IFileDeleteOperationActionHistoryStore historyStore,
        List<Exception> failures,
        string failureCode,
        string failureMessage)
    {
        var failure = new FileOperationFailure(
            failureCode,
            failureMessage,
            barrierScope.FinalEvidence.CanonicalSourcePath,
            Retryable: false);
        try
        {
            var recoveryHistory = await historyStore
                .MarkMutationRecoveryRequiredAsync(
                    barrierScope.BarrierHistory.OperationId,
                    barrierScope.Ordinal,
                    failure,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ValidateRecoveryHistory(
                barrierScope,
                recoveryHistory,
                failureCode);
        }
        catch (Exception recoveryException)
        {
            failures.Add(recoveryException);
        }
    }

    private static void ValidateBarrierInputs(
        FileDeleteOperationMutationBarrierScope barrierScope)
    {
        var history = barrierScope.BarrierHistory;
        var evidence = barrierScope.FinalEvidence;
        var ordinal = barrierScope.Ordinal;
        if (history.OperationId == Guid.Empty ||
            history.TerminalState.HasValue ||
            history.CompletedAtUtc.HasValue ||
            ordinal < 0 ||
            ordinal >= history.Entries.Count ||
            !evidence.IsBoundTo(barrierScope.Authorization, ordinal) ||
            history.AuthorizationId != barrierScope.Authorization.AuthorizationId ||
            history.OperationId != barrierScope.Authorization.PlanId ||
            !PathEquals(history.CanonicalSourceDirectoryPath, evidence.CanonicalSourceDirectoryPath) ||
            history.SourceDirectoryIdentity != evidence.SourceDirectoryIdentity)
        {
            throw new InvalidOperationException(
                "Delete mutation commit requires exact non-terminal barrier provenance bound to the final evidence.");
        }

        var entry = history.Entries[ordinal];
        if (entry.State != FileDeleteOperationActionEntryState.MutationStarted ||
            !entry.MutationStartedAtUtc.HasValue ||
            entry.CompletedAtUtc.HasValue ||
            entry.Failure is not null ||
            !PathEquals(entry.CanonicalSourcePath, evidence.CanonicalSourcePath) ||
            entry.SourceIdentity != evidence.SourceIdentity)
        {
            throw new InvalidOperationException(
                "Delete mutation commit requires the exact selected durable MutationStarted entry.");
        }
    }

    private static void ValidateCommittedHistory(
        FileDeleteOperationMutationBarrierScope barrierScope,
        FileDeleteOperationActionHistory history)
    {
        ValidateStaticHistory(barrierScope, history);
        var prior = barrierScope.BarrierHistory;
        var ordinal = barrierScope.Ordinal;
        for (var index = 0; index < history.Entries.Count; index++)
        {
            var current = history.Entries[index];
            var previous = prior.Entries[index];
            if (index != ordinal)
            {
                if (current != previous)
                {
                    throw new InvalidOperationException(
                        "Delete mutation commit changed durable history for an unrelated entry.");
                }
                continue;
            }

            if (current.Ordinal != previous.Ordinal ||
                current.Entry != previous.Entry ||
                !PathEquals(current.CanonicalSourcePath, previous.CanonicalSourcePath) ||
                current.SourceIdentity != previous.SourceIdentity ||
                current.State != FileDeleteOperationActionEntryState.Committed ||
                current.MutationStartedAtUtc != previous.MutationStartedAtUtc ||
                !current.CompletedAtUtc.HasValue ||
                current.Failure is not null ||
                current.SourceIdentity != barrierScope.FinalEvidence.SourceIdentity)
            {
                throw new InvalidOperationException(
                    "Delete mutation commit history does not contain the exact selected identity-bound Committed entry.");
            }
        }
    }

    private static bool TryValidateCommittedHistory(
        FileDeleteOperationMutationBarrierScope barrierScope,
        FileDeleteOperationActionHistory history,
        out Exception? validationException)
    {
        try
        {
            ValidateCommittedHistory(barrierScope, history);
            validationException = null;
            return true;
        }
        catch (Exception exception)
        {
            validationException = exception;
            return false;
        }
    }

    private static void ValidateRecoveryHistory(
        FileDeleteOperationMutationBarrierScope barrierScope,
        FileDeleteOperationActionHistory history,
        string? expectedFailureCode)
    {
        ValidateStaticHistory(barrierScope, history);
        var prior = barrierScope.BarrierHistory;
        var ordinal = barrierScope.Ordinal;
        for (var index = 0; index < history.Entries.Count; index++)
        {
            var current = history.Entries[index];
            var previous = prior.Entries[index];
            if (index != ordinal)
            {
                if (current != previous)
                {
                    throw new InvalidOperationException(
                        "Delete mutation recovery changed durable history for an unrelated entry.");
                }
                continue;
            }

            if (current.Ordinal != previous.Ordinal ||
                current.Entry != previous.Entry ||
                !PathEquals(current.CanonicalSourcePath, previous.CanonicalSourcePath) ||
                current.SourceIdentity != previous.SourceIdentity ||
                current.State != FileDeleteOperationActionEntryState.RecoveryRequired ||
                current.MutationStartedAtUtc != previous.MutationStartedAtUtc ||
                !current.CompletedAtUtc.HasValue ||
                current.Failure is null ||
                (expectedFailureCode is not null &&
                    !string.Equals(current.Failure.Code, expectedFailureCode, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Delete mutation recovery history does not contain the exact selected recovery-sensitive entry.");
            }
        }
    }

    private static bool TryValidateRecoveryHistory(
        FileDeleteOperationMutationBarrierScope barrierScope,
        FileDeleteOperationActionHistory history,
        string? expectedFailureCode,
        out Exception? validationException)
    {
        try
        {
            ValidateRecoveryHistory(barrierScope, history, expectedFailureCode);
            validationException = null;
            return true;
        }
        catch (Exception exception)
        {
            validationException = exception;
            return false;
        }
    }

    private static bool IsExactBarrierHistory(
        FileDeleteOperationMutationBarrierScope barrierScope,
        FileDeleteOperationActionHistory history)
    {
        try
        {
            ValidateStaticHistory(barrierScope, history);
        }
        catch
        {
            return false;
        }

        var prior = barrierScope.BarrierHistory;
        for (var index = 0; index < prior.Entries.Count; index++)
        {
            if (history.Entries[index] != prior.Entries[index])
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateStaticHistory(
        FileDeleteOperationMutationBarrierScope barrierScope,
        FileDeleteOperationActionHistory history)
    {
        var prior = barrierScope.BarrierHistory;
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
                "Delete mutation settlement changed operation-level authorization/provenance evidence.");
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
