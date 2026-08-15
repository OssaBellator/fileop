using System;

namespace FileOp.Core.Indexing;

public enum IndexRebuildPublicationAttemptState
{
    Prepared,
    SwapStarted,
    Published,
    AbandonedBeforeSwap,
    RecoveryRequired,
}

/// <summary>
/// Non-authorizing evidence that the SQLite/live-index namespace has been quiesced strongly
/// enough for a later platform publication primitive to cross the filesystem swap barrier.
///
/// The Windows implementation must produce this while holding both the in-process volume
/// operation gate and the cross-process maintenance lease. Because the live index uses WAL,
/// shared cache and connection pooling, simply disposing one command/connection is not
/// sufficient: live and shadow WAL state must be checkpointed, pooled connections cleared,
/// and WAL/SHM sidecars no longer capable of carrying unmerged state.
/// </summary>
public sealed record IndexRebuildPublicationQuiescenceEvidence(
    string LiveDatabasePath,
    string ShadowDatabasePath,
    bool LocalVolumeOperationGateHeld,
    bool CrossProcessMaintenanceLeaseHeld,
    bool LiveWalCheckpointComplete,
    bool ShadowWalCheckpointComplete,
    bool SqliteConnectionPoolsCleared,
    bool LiveWalAndShmSidecarsQuiesced,
    bool ShadowWalAndShmSidecarsQuiesced)
{
    public bool CanStartFilesystemSwap =>
        LocalVolumeOperationGateHeld &&
        CrossProcessMaintenanceLeaseHeld &&
        LiveWalCheckpointComplete &&
        ShadowWalCheckpointComplete &&
        SqliteConnectionPoolsCleared &&
        LiveWalAndShmSidecarsQuiesced &&
        ShadowWalAndShmSidecarsQuiesced &&
        !string.IsNullOrWhiteSpace(LiveDatabasePath) &&
        !string.IsNullOrWhiteSpace(ShadowDatabasePath) &&
        !string.Equals(
            LiveDatabasePath,
            ShadowDatabasePath,
            StringComparison.OrdinalIgnoreCase);

    public bool GrantsFilesystemMutationAuthority => false;
}

/// <summary>
/// Immutable evidence/state for one future live/shadow index publication attempt.
///
/// This type performs no filesystem or SQLite mutation. It exists so a later Windows
/// publication primitive cannot treat a failed post-swap-start operation as a safe retry.
/// The attempt never grants automatic retry, rollback, cleanup, delete or swap authority.
/// </summary>
public sealed record IndexRebuildPublicationAttempt(
    Guid AttemptId,
    string LiveDatabasePath,
    string ShadowDatabasePath,
    IndexRebuildPublicationAttemptState State,
    bool LiveSnapshotWasReadableAtPreparation,
    bool ShadowCheckpointWasValidAtPreparation,
    bool ExclusivePublicationLeaseWasHeldAtPreparation,
    IndexRebuildPublicationQuiescenceEvidence? QuiescenceAtSwapStart,
    string? FailureSummary)
{
    public bool SwapMayHaveChangedLiveSnapshot =>
        State is IndexRebuildPublicationAttemptState.SwapStarted or
        IndexRebuildPublicationAttemptState.Published or
        IndexRebuildPublicationAttemptState.RecoveryRequired;

    public bool RequiresRecovery =>
        State == IndexRebuildPublicationAttemptState.RecoveryRequired;

    public bool GrantsAutomaticRetryAuthority => false;

    public bool GrantsRollbackAuthority => false;

    public bool GrantsCleanupAuthority => false;

    public bool GrantsFilesystemMutationAuthority => false;
}

public static class IndexRebuildPublicationTransactionPolicy
{
    public static IndexRebuildPublicationAttempt Prepare(
        Guid attemptId,
        IndexRebuildPublicationState publicationState)
    {
        ArgumentNullException.ThrowIfNull(publicationState);
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException(
                "Index publication attempt ID must be non-empty.",
                nameof(attemptId));
        }
        if (!publicationState.CanPublish)
        {
            throw new InvalidOperationException(
                "Index publication cannot be prepared until the shadow checkpoint is verified, the live snapshot remains readable, and the exclusive publication lease is held.");
        }

        return new IndexRebuildPublicationAttempt(
            attemptId,
            publicationState.LiveDatabasePath,
            publicationState.ShadowDatabasePath,
            IndexRebuildPublicationAttemptState.Prepared,
            publicationState.LiveSnapshotReadable,
            publicationState.ShadowHasValidCheckpoint,
            publicationState.ExclusivePublicationLeaseHeld,
            QuiescenceAtSwapStart: null,
            FailureSummary: null);
    }

    public static IndexRebuildPublicationAttempt MarkSwapStarted(
        IndexRebuildPublicationAttempt attempt,
        IndexRebuildPublicationState currentPublicationState,
        IndexRebuildPublicationQuiescenceEvidence quiescence)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(currentPublicationState);
        ArgumentNullException.ThrowIfNull(quiescence);
        RequireState(attempt, IndexRebuildPublicationAttemptState.Prepared);
        RequireSamePublicationBinding(attempt, currentPublicationState);
        RequireSamePublicationBinding(attempt, quiescence);
        if (!currentPublicationState.CanPublish)
        {
            throw new InvalidOperationException(
                "Index publication swap cannot start after the verified checkpoint, readable live snapshot, or exclusive publication lease evidence has been lost.");
        }
        if (!quiescence.CanStartFilesystemSwap)
        {
            throw new InvalidOperationException(
                "Index publication swap cannot start until the local volume operation gate and cross-process maintenance lease are held, both WAL files are checkpointed, SQLite connection pools are cleared, and live/shadow WAL/SHM sidecars are quiesced.");
        }

        return attempt with
        {
            State = IndexRebuildPublicationAttemptState.SwapStarted,
            QuiescenceAtSwapStart = quiescence,
            FailureSummary = null,
        };
    }

    public static IndexRebuildPublicationAttempt MarkPublished(
        IndexRebuildPublicationAttempt attempt,
        IndexRebuildPublicationState currentPublicationState)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(currentPublicationState);
        RequireState(attempt, IndexRebuildPublicationAttemptState.SwapStarted);
        RequireSamePublicationBinding(attempt, currentPublicationState);
        if (attempt.QuiescenceAtSwapStart is null ||
            !attempt.QuiescenceAtSwapStart.CanStartFilesystemSwap)
        {
            throw new InvalidOperationException(
                "Index publication cannot be committed without the exact SQLite quiescence evidence that authorized crossing the swap barrier.");
        }

        return attempt with
        {
            State = IndexRebuildPublicationAttemptState.Published,
            FailureSummary = null,
        };
    }

    public static IndexRebuildPublicationAttempt AbandonBeforeSwap(
        IndexRebuildPublicationAttempt attempt,
        string failureSummary)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        RequireState(attempt, IndexRebuildPublicationAttemptState.Prepared);
        return attempt with
        {
            State = IndexRebuildPublicationAttemptState.AbandonedBeforeSwap,
            FailureSummary = RequireFailureSummary(failureSummary),
        };
    }

    public static IndexRebuildPublicationAttempt MarkRecoveryRequired(
        IndexRebuildPublicationAttempt attempt,
        string failureSummary)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        RequireState(attempt, IndexRebuildPublicationAttemptState.SwapStarted);
        return attempt with
        {
            State = IndexRebuildPublicationAttemptState.RecoveryRequired,
            FailureSummary = RequireFailureSummary(failureSummary),
        };
    }

    private static void RequireSamePublicationBinding(
        IndexRebuildPublicationAttempt attempt,
        IndexRebuildPublicationState publicationState)
    {
        if (!string.Equals(
                attempt.LiveDatabasePath,
                publicationState.LiveDatabasePath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                attempt.ShadowDatabasePath,
                publicationState.ShadowDatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Index publication attempt is not bound to the current live/shadow database paths.");
        }
    }

    private static void RequireSamePublicationBinding(
        IndexRebuildPublicationAttempt attempt,
        IndexRebuildPublicationQuiescenceEvidence quiescence)
    {
        if (!string.Equals(
                attempt.LiveDatabasePath,
                quiescence.LiveDatabasePath,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                attempt.ShadowDatabasePath,
                quiescence.ShadowDatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Index publication quiescence evidence is not bound to this attempt's live/shadow database paths.");
        }
    }

    private static void RequireState(
        IndexRebuildPublicationAttempt attempt,
        IndexRebuildPublicationAttemptState requiredState)
    {
        if (attempt.State != requiredState)
        {
            throw new InvalidOperationException(
                $"Index publication attempt must be {requiredState} for this transition; current state is {attempt.State}.");
        }
    }

    private static string RequireFailureSummary(string failureSummary)
    {
        if (string.IsNullOrWhiteSpace(failureSummary))
        {
            throw new ArgumentException(
                "Index publication failure settlement requires a non-empty summary.",
                nameof(failureSummary));
        }

        return failureSummary.Trim();
    }
}
