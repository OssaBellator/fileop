using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

/// <summary>
/// Live, session-only authority for one delete entry after the exact final lease has been
/// transferred into Core and the durable Pending -> MutationStarted barrier has succeeded.
/// This scope still performs no filesystem mutation and exposes no lease, request, or raw handle.
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

    public FileDeleteOperationActionEntry BarrierEntry => BarrierHistory.Entries[Ordinal];

    public FileDeleteOperationUserAuthorizationReceipt Authorization => PriorBindingEvidence.Authorization;

    public int Ordinal => PriorBindingEvidence.Ordinal;

    public bool FinalLeaseOwnershipTransferred => true;

    public bool MutationBarrierSatisfied => true;

    public bool DeleteMutationAuthorized => true;

    public bool DeleteMutationPerformed => false;

    public bool FinalLeaseHeld => Volatile.Read(ref _lease) is not null;

    public bool DeleteAccessCapabilityHeld
    {
        get
        {
            var lease = Volatile.Read(ref _lease);
            return lease is not null && lease.DeleteAccessCapabilityHeld;
        }
    }

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
/// Claims the durable delete mutation barrier while keeping private ownership of the exact final
/// provider lease. No filesystem mutation is invoked by this boundary.
/// </summary>
public static class FileDeleteOperationMutationBarrier
{
    private const string BarrierValidationFailureCode = "delete.mutation-barrier.validation";

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
            finalLeaseScope.DeleteMutationPerformed ||
            !finalLeaseScope.PriorReadOnlyLeaseReleaseObserved ||
            !finalLeaseScope.FinalLeaseProviderAcquisitionObserved ||
            !finalLeaseScope.FinalLeaseHeld ||
            !finalLeaseScope.DeleteAccessCapabilityHeld)
        {
            throw new InvalidOperationException(
                "Delete mutation barrier requires one live, non-authorizing final lease scope before the durable barrier.");
        }

        var priorBinding = finalLeaseScope.PriorBindingEvidence;
        var finalEvidence = finalLeaseScope.FinalEvidence;
        var authorization = priorBinding.Authorization;
        var ordinal = priorBinding.Ordinal;
        if (!finalEvidence.IsBoundTo(authorization, ordinal) ||
            finalEvidence.DeleteMutationAuthorized)
        {
            throw new InvalidOperationException(
                "Delete mutation barrier requires final evidence bound to the exact authorization receipt and ordinal.");
        }

        // Transfer ownership before touching durable history. A retained alias to the old public
        // scope becomes inert and can no longer dispose the lease under this barrier attempt.
        var lease = await finalLeaseScope.DetachForMutationBarrierAsync().ConfigureAwait(false);
        if (finalLeaseScope.FinalLeaseHeld || finalLeaseScope.DeleteAccessCapabilityHeld)
        {
            var transferException = new InvalidOperationException(
                "Final delete lease ownership transfer did not make the old scope inert.");
            await ReleaseBeforeBarrierFailureAsync(lease, transferException).ConfigureAwait(false);
            throw transferException;
        }
        if (lease.DeleteMutationAuthorized || !lease.DeleteAccessCapabilityHeld)
        {
            var capabilityException = new InvalidOperationException(
                "Transferred final delete lease no longer holds one non-authorizing delete-access capability.");
            await ReleaseBeforeBarrierFailureAsync(lease, capabilityException).ConfigureAwait(false);
            throw capabilityException;
        }

        FileDeleteOperationActionHistory barrierHistory;
        try
        {
            barrierHistory = await historyStore
                .MarkMutationStartedAsync(authorization.PlanId, ordinal, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception transitionException)
        {
            await ReleaseBeforeBarrierFailureAsync(lease, transitionException).ConfigureAwait(false);
            throw;
        }

        try
        {
            ValidateBarrierHistory(priorBinding, finalEvidence, barrierHistory);
            if (lease.DeleteMutationAuthorized || !lease.DeleteAccessCapabilityHeld)
            {
                throw new InvalidOperationException(
                    "Final delete capability was lost or became mutation-authorizing while crossing the durable barrier.");
            }
        }
        catch (Exception validationException)
        {
            await MarkRecoveryAndReleaseAsync(
                    historyStore,
                    authorization.PlanId,
                    ordinal,
                    finalEvidence.CanonicalSourcePath,
                    lease,
                    validationException)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "Delete mutation barrier returned inconsistent current history and was marked recovery-required.",
                validationException);
        }

        return new FileDeleteOperationMutationBarrierScope(
            priorBinding,
            finalEvidence,
            barrierHistory,
            lease);
    }

    private static void ValidateBarrierHistory(
        FileDeleteOperationHistoryBindingEvidence priorBinding,
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence,
        FileDeleteOperationActionHistory barrierHistory)
    {
        ArgumentNullException.ThrowIfNull(barrierHistory);
        var authorization = priorBinding.Authorization;
        var priorHistory = priorBinding.HistorySnapshot;
        var ordinal = priorBinding.Ordinal;

        if (barrierHistory.DeleteMutationAuthorized || barrierHistory.IsTerminal ||
            barrierHistory.OperationId != authorization.PlanId ||
            barrierHistory.AuthorizationId != authorization.AuthorizationId ||
            barrierHistory.Entries.Count != authorization.Items.Count ||
            barrierHistory.Entries.Count != priorHistory.Entries.Count)
        {
            throw new InvalidOperationException(
                "Durable delete barrier history does not match the authorized non-terminal operation shape.");
        }
        if (!string.Equals(barrierHistory.SourcePaneId, priorHistory.SourcePaneId, StringComparison.Ordinal) ||
            barrierHistory.SourceTabId != priorHistory.SourceTabId ||
            barrierHistory.QueuedAtUtc != priorHistory.QueuedAtUtc ||
            barrierHistory.ValidatedAtUtc != priorHistory.ValidatedAtUtc ||
            barrierHistory.AuthorizedAtUtc != priorHistory.AuthorizedAtUtc ||
            barrierHistory.StartedAtUtc != priorHistory.StartedAtUtc ||
            barrierHistory.CompletedAtUtc is not null ||
            !PathEquals(barrierHistory.CanonicalSourceDirectoryPath, priorHistory.CanonicalSourceDirectoryPath) ||
            barrierHistory.SourceDirectoryIdentity != priorHistory.SourceDirectoryIdentity)
        {
            throw new InvalidOperationException(
                "Durable delete barrier operation provenance changed while claiming the mutation barrier.");
        }

        for (var index = 0; index < barrierHistory.Entries.Count; index++)
        {
            var authorizedItem = authorization.Items[index];
            var current = barrierHistory.Entries[index];
            var previous = priorHistory.Entries[index];
            if (current.Ordinal != index ||
                current.Entry != authorizedItem.Entry ||
                !PathEquals(current.CanonicalSourcePath, authorizedItem.CanonicalPath) ||
                current.SourceIdentity != authorizedItem.Identity)
            {
                throw new InvalidOperationException(
                    "Durable delete barrier entry provenance no longer matches the exact authorization evidence.");
            }

            if (index != ordinal)
            {
                if (current != previous)
                {
                    throw new InvalidOperationException(
                        "Another delete-history entry changed while claiming this mutation barrier.");
                }
                continue;
            }

            if (previous.State != FileDeleteOperationActionEntryState.Pending ||
                current.State != FileDeleteOperationActionEntryState.MutationStarted ||
                current.MutationStartedAtUtc is null ||
                current.CompletedAtUtc is not null ||
                current.Failure is not null)
            {
                throw new InvalidOperationException(
                    "Selected delete-history entry did not make the exact Pending -> MutationStarted transition.");
            }
            if (!PathEquals(current.CanonicalSourcePath, finalEvidence.CanonicalSourcePath) ||
                current.SourceIdentity != finalEvidence.SourceIdentity)
            {
                throw new InvalidOperationException(
                    "Durable delete barrier entry does not match the held final file identity evidence.");
            }
        }
    }

    private static async ValueTask MarkRecoveryAndReleaseAsync(
        IFileDeleteOperationActionHistoryStore historyStore,
        Guid operationId,
        int ordinal,
        string sourcePath,
        IFileDeleteOperationFinalMutationLease lease,
        Exception validationException)
    {
        var failures = new List<Exception> { validationException };
        var failure = new FileOperationFailure(
            BarrierValidationFailureCode,
            "Durable delete mutation barrier returned inconsistent history after MutationStarted.",
            sourcePath,
            Retryable: false);

        try
        {
            await historyStore
                .MarkMutationRecoveryRequiredAsync(
                    operationId,
                    ordinal,
                    failure,
                    CancellationToken.None)
                .ConfigureAwait(false);
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
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Delete mutation barrier validation failed and recovery settlement was incomplete.",
                failures);
        }
    }

    private static async ValueTask ReleaseBeforeBarrierFailureAsync(
        IFileDeleteOperationFinalMutationLease lease,
        Exception primaryException)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposalException)
        {
            throw new AggregateException(
                "Delete mutation barrier failed before success and releasing the transferred final lease also failed.",
                primaryException,
                disposalException);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
