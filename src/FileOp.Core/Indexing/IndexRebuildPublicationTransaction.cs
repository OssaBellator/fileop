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
            FailureSummary: null);
    }

    public static IndexRebuildPublicationAttempt MarkSwapStarted(
        IndexRebuildPublicationAttempt attempt,
        IndexRebuildPublicationState currentPublicationState)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(currentPublicationState);
        RequireState(attempt, IndexRebuildPublicationAttemptState.Prepared);
        RequireSamePublicationBinding(attempt, currentPublicationState);
        if (!currentPublicationState.CanPublish)
        {
            throw new InvalidOperationException(
                "Index publication swap cannot start after the verified checkpoint, readable live snapshot, or exclusive publication lease evidence has been lost.");
        }

        return attempt with
        {
            State = IndexRebuildPublicationAttemptState.SwapStarted,
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
