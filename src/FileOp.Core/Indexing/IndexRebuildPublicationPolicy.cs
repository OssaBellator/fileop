using System;

namespace FileOp.Core.Indexing;

public enum IndexRebuildSnapshotState
{
    BuildingShadow,
    ShadowCheckpointVerified,
    Published,
    Abandoned,
}

/// <summary>
/// Defines the publication contract for a future native-index rebuild that keeps the
/// last valid snapshot readable until a replacement database is checkpointed and swapped.
/// This policy does not itself move database files.
/// </summary>
public sealed record IndexRebuildPublicationState(
    string LiveDatabasePath,
    string ShadowDatabasePath,
    IndexRebuildSnapshotState State,
    bool LiveSnapshotReadable,
    bool ShadowHasValidCheckpoint,
    bool ExclusivePublicationLeaseHeld)
{
    public bool CanPublish =>
        State == IndexRebuildSnapshotState.ShadowCheckpointVerified &&
        LiveSnapshotReadable &&
        ShadowHasValidCheckpoint &&
        ExclusivePublicationLeaseHeld &&
        !string.Equals(LiveDatabasePath, ShadowDatabasePath, StringComparison.OrdinalIgnoreCase);
}

public static class IndexRebuildPublicationPolicy
{
    public static IndexRebuildPublicationState Begin(string liveDatabasePath, string shadowDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(liveDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowDatabasePath);
        if (string.Equals(liveDatabasePath, shadowDatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A shadow rebuild must use a database path distinct from the live snapshot.");
        }

        return new(
            liveDatabasePath,
            shadowDatabasePath,
            IndexRebuildSnapshotState.BuildingShadow,
            LiveSnapshotReadable: true,
            ShadowHasValidCheckpoint: false,
            ExclusivePublicationLeaseHeld: false);
    }

    public static IndexRebuildPublicationState MarkVerified(
        IndexRebuildPublicationState state,
        bool exclusivePublicationLeaseHeld)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.State != IndexRebuildSnapshotState.BuildingShadow || !state.LiveSnapshotReadable)
        {
            throw new InvalidOperationException("Only a building shadow with an intact live snapshot can become publishable.");
        }

        return state with
        {
            State = IndexRebuildSnapshotState.ShadowCheckpointVerified,
            ShadowHasValidCheckpoint = true,
            ExclusivePublicationLeaseHeld = exclusivePublicationLeaseHeld,
        };
    }

    public static IndexRebuildPublicationState MarkPublished(IndexRebuildPublicationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.CanPublish)
        {
            throw new InvalidOperationException(
                "A rebuilt index cannot replace the live snapshot before checkpoint verification and an exclusive publication lease.");
        }

        return state with
        {
            State = IndexRebuildSnapshotState.Published,
            LiveSnapshotReadable = true,
            ExclusivePublicationLeaseHeld = false,
        };
    }

    public static IndexRebuildPublicationState Abandon(IndexRebuildPublicationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.State == IndexRebuildSnapshotState.Published)
        {
            throw new InvalidOperationException("A published rebuild cannot be abandoned as an unpublished shadow.");
        }

        return state with
        {
            State = IndexRebuildSnapshotState.Abandoned,
            LiveSnapshotReadable = true,
            ShadowHasValidCheckpoint = false,
            ExclusivePublicationLeaseHeld = false,
        };
    }
}
