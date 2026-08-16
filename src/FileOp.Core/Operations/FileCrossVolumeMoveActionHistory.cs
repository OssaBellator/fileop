using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum FileCrossVolumeMoveEntryState
{
    Pending,
    CopyMutationStarted,
    DestinationCommitted,
    SourceDeleteStarted,
    Moved,
    Skipped,
    Failed,
    RecoveryRequired,
}

public enum FileCrossVolumeMoveTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
    RecoveryRequired,
}

/// <summary>
/// Durable evidence for one entry of a cross-volume Move. A committed destination is a
/// safe non-destructive checkpoint: the new file is proven while the original source is
/// still retained. Source deletion receives its own later durable barrier.
/// </summary>
public sealed record FileCrossVolumeMoveActionEntry(
    int Ordinal,
    FileOperationEntry Entry,
    string CanonicalSourcePath,
    string CanonicalDestinationPath,
    FileCrossVolumeMoveEntryState State,
    FileIdentity SourceIdentity,
    FileIdentity? DestinationIdentity,
    FileContentFingerprint? DestinationContentFingerprint,
    DateTimeOffset? CopyMutationStartedAtUtc,
    DateTimeOffset? DestinationCommittedAtUtc,
    DateTimeOffset? SourceDeleteStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    FileOperationFailure? Failure)
{
    /// <summary>
    /// True only when the journal proves the normal DestinationCommitted checkpoint was
    /// reached before any later terminal/recovery transition. Copy-barrier recovery may
    /// retain observed destination identity/content evidence without proving that safe
    /// checkpoint and must not be upgraded into this property.
    /// </summary>
    public bool DestinationIsDurablyCommitted =>
        State is FileCrossVolumeMoveEntryState.DestinationCommitted or
            FileCrossVolumeMoveEntryState.SourceDeleteStarted or
            FileCrossVolumeMoveEntryState.Moved ||
        (State == FileCrossVolumeMoveEntryState.Failed &&
            DestinationIdentity.HasValue &&
            DestinationContentFingerprint is not null) ||
        (State == FileCrossVolumeMoveEntryState.RecoveryRequired &&
            SourceDeleteStartedAtUtc.HasValue &&
            DestinationIdentity.HasValue &&
            DestinationContentFingerprint is not null);

    /// <summary>
    /// Recovery-only observation that a destination identity/content pair was captured.
    /// This does not imply that canonical destination commit completed successfully and
    /// grants no cleanup, replay, or source-delete authority.
    /// </summary>
    public bool HasDestinationRecoveryEvidence =>
        State == FileCrossVolumeMoveEntryState.RecoveryRequired &&
        DestinationIdentity.HasValue &&
        DestinationContentFingerprint is not null;

    /// <summary>
    /// True when the durable source-delete barrier was crossed but normal source-delete
    /// completion is not proven. RecoveryRequired retains that uncertainty through its
    /// SourceDeleteStarted timestamp even though the entry is no longer in the live
    /// SourceDeleteStarted state.
    /// </summary>
    public bool SourceDeleteBarrierMayBeUnresolved =>
        State == FileCrossVolumeMoveEntryState.SourceDeleteStarted ||
        (State == FileCrossVolumeMoveEntryState.RecoveryRequired &&
            SourceDeleteStartedAtUtc.HasValue);
}

public sealed record FileCrossVolumeMoveActionHistory
{
    public FileCrossVolumeMoveActionHistory(
        Guid operationId,
        DateTimeOffset queuedAtUtc,
        DateTimeOffset validatedAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        FileOperationCollisionPolicy collisionPolicy,
        string sourceDirectoryPath,
        string destinationDirectoryPath,
        string canonicalSourceDirectoryPath,
        string canonicalDestinationDirectoryPath,
        FileIdentity sourceDirectoryIdentity,
        FileIdentity destinationDirectoryIdentity,
        FileCrossVolumeMoveTerminalState? terminalState,
        IEnumerable<FileCrossVolumeMoveActionEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDestinationDirectoryPath);
        ArgumentNullException.ThrowIfNull(entries);
        if (sourceDirectoryIdentity.VolumeSerialNumber == destinationDirectoryIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Cross-volume Move history requires source and destination roots on different filesystem volumes.");
        }

        var snapshot = entries.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "Cross-volume Move history requires at least one regular-file entry.",
                nameof(entries));
        }

        for (var ordinal = 0; ordinal < snapshot.Length; ordinal++)
        {
            ValidateEntry(
                snapshot[ordinal],
                ordinal,
                collisionPolicy,
                sourceDirectoryIdentity,
                destinationDirectoryIdentity);
        }

        ValidateEntryChronology(snapshot);
        ValidateTerminalState(snapshot, terminalState, completedAtUtc);

        OperationId = operationId;
        QueuedAtUtc = queuedAtUtc;
        ValidatedAtUtc = validatedAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        CollisionPolicy = collisionPolicy;
        SourceDirectoryPath = sourceDirectoryPath;
        DestinationDirectoryPath = destinationDirectoryPath;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        CanonicalDestinationDirectoryPath = canonicalDestinationDirectoryPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        DestinationDirectoryIdentity = destinationDirectoryIdentity;
        TerminalState = terminalState;
        Entries = Array.AsReadOnly(snapshot);
    }

    public Guid OperationId { get; }

    public DateTimeOffset QueuedAtUtc { get; }

    public DateTimeOffset ValidatedAtUtc { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public FileOperationCollisionPolicy CollisionPolicy { get; }

    public string SourceDirectoryPath { get; }

    public string DestinationDirectoryPath { get; }

    public string CanonicalSourceDirectoryPath { get; }

    public string CanonicalDestinationDirectoryPath { get; }

    public FileIdentity SourceDirectoryIdentity { get; }

    public FileIdentity DestinationDirectoryIdentity { get; }

    public FileCrossVolumeMoveTerminalState? TerminalState { get; }

    public IReadOnlyList<FileCrossVolumeMoveActionEntry> Entries { get; }

    public bool RequiresRecovery =>
        TerminalState == FileCrossVolumeMoveTerminalState.RecoveryRequired ||
        Entries.Any(static entry =>
            entry.State is FileCrossVolumeMoveEntryState.CopyMutationStarted or
                FileCrossVolumeMoveEntryState.SourceDeleteStarted or
                FileCrossVolumeMoveEntryState.RecoveryRequired);

    public bool HasRetainedSourceDuplicates =>
        Entries.Any(static entry =>
            (entry.State is FileCrossVolumeMoveEntryState.DestinationCommitted or
                FileCrossVolumeMoveEntryState.Failed) &&
            entry.DestinationIdentity.HasValue &&
            entry.DestinationContentFingerprint is not null);

    private static void ValidateEntry(
        FileCrossVolumeMoveActionEntry entry,
        int expectedOrdinal,
        FileOperationCollisionPolicy collisionPolicy,
        FileIdentity sourceDirectoryIdentity,
        FileIdentity destinationDirectoryIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.CanonicalSourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.CanonicalDestinationPath);
        if (entry.Ordinal != expectedOrdinal)
        {
            throw new ArgumentException(
                "Cross-volume Move history entries must preserve exact contiguous plan order.");
        }
        if (entry.Entry.IsDirectory)
        {
            throw new ArgumentException(
                "Cross-volume Move history supports regular files only.");
        }
        if (entry.SourceIdentity.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Cross-volume Move source entry identity must remain bound to the durable source-root volume.");
        }
        if (entry.DestinationIdentity is FileIdentity destinationIdentity &&
            destinationIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Cross-volume Move destination evidence must remain bound to the durable destination-root volume.");
        }

        var hasDestinationEvidence =
            entry.DestinationIdentity.HasValue &&
            entry.DestinationContentFingerprint is not null;
        if (entry.DestinationIdentity.HasValue !=
            (entry.DestinationContentFingerprint is not null))
        {
            throw new ArgumentException(
                "Cross-volume Move destination identity and content fingerprint must be recorded together.");
        }

        switch (entry.State)
        {
            case FileCrossVolumeMoveEntryState.Pending:
                RequireNoDestinationEvidence(entry, hasDestinationEvidence);
                RequireNull(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                RequireNull(entry.DestinationCommittedAtUtc, nameof(entry.DestinationCommittedAtUtc));
                RequireNull(entry.SourceDeleteStartedAtUtc, nameof(entry.SourceDeleteStartedAtUtc));
                RequireNull(entry.CompletedAtUtc, nameof(entry.CompletedAtUtc));
                RequireNull(entry.Failure, nameof(entry.Failure));
                break;

            case FileCrossVolumeMoveEntryState.CopyMutationStarted:
                RequireNoDestinationEvidence(entry, hasDestinationEvidence);
                RequirePresent(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                RequireNull(entry.DestinationCommittedAtUtc, nameof(entry.DestinationCommittedAtUtc));
                RequireNull(entry.SourceDeleteStartedAtUtc, nameof(entry.SourceDeleteStartedAtUtc));
                RequireNull(entry.CompletedAtUtc, nameof(entry.CompletedAtUtc));
                RequireNull(entry.Failure, nameof(entry.Failure));
                break;

            case FileCrossVolumeMoveEntryState.DestinationCommitted:
                RequireDestinationEvidence(entry, hasDestinationEvidence);
                RequirePresent(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                RequirePresent(entry.DestinationCommittedAtUtc, nameof(entry.DestinationCommittedAtUtc));
                RequireNull(entry.SourceDeleteStartedAtUtc, nameof(entry.SourceDeleteStartedAtUtc));
                RequireNull(entry.CompletedAtUtc, nameof(entry.CompletedAtUtc));
                RequireNull(entry.Failure, nameof(entry.Failure));
                break;

            case FileCrossVolumeMoveEntryState.SourceDeleteStarted:
                RequireDestinationEvidence(entry, hasDestinationEvidence);
                RequirePresent(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                RequirePresent(entry.DestinationCommittedAtUtc, nameof(entry.DestinationCommittedAtUtc));
                RequirePresent(entry.SourceDeleteStartedAtUtc, nameof(entry.SourceDeleteStartedAtUtc));
                RequireNull(entry.CompletedAtUtc, nameof(entry.CompletedAtUtc));
                RequireNull(entry.Failure, nameof(entry.Failure));
                break;

            case FileCrossVolumeMoveEntryState.Moved:
                RequireDestinationEvidence(entry, hasDestinationEvidence);
                RequirePresent(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                RequirePresent(entry.DestinationCommittedAtUtc, nameof(entry.DestinationCommittedAtUtc));
                RequirePresent(entry.SourceDeleteStartedAtUtc, nameof(entry.SourceDeleteStartedAtUtc));
                RequirePresent(entry.CompletedAtUtc, nameof(entry.CompletedAtUtc));
                RequireNull(entry.Failure, nameof(entry.Failure));
                break;

            case FileCrossVolumeMoveEntryState.Skipped:
                if (collisionPolicy != FileOperationCollisionPolicy.Skip)
                {
                    throw new ArgumentException(
                        "A cross-volume Move entry may be Skipped only under explicit Skip collision policy.");
                }
                RequireNoDestinationEvidence(entry, hasDestinationEvidence);
                RequireNull(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                RequireNull(entry.DestinationCommittedAtUtc, nameof(entry.DestinationCommittedAtUtc));
                RequireNull(entry.SourceDeleteStartedAtUtc, nameof(entry.SourceDeleteStartedAtUtc));
                RequirePresent(entry.CompletedAtUtc, nameof(entry.CompletedAtUtc));
                RequireNull(entry.Failure, nameof(entry.Failure));
                break;

            case FileCrossVolumeMoveEntryState.Failed:
                if (entry.SourceDeleteStartedAtUtc.HasValue)
                {
                    throw new ArgumentException(
                        "A cross-volume Move failure after the source-delete barrier must be RecoveryRequired, not Failed.");
                }
                RequirePresent(entry.CompletedAtUtc, nameof(entry.CompletedAtUtc));
                RequirePresent(entry.Failure, nameof(entry.Failure));
                if (entry.DestinationCommittedAtUtc.HasValue != hasDestinationEvidence)
                {
                    throw new ArgumentException(
                        "A Failed cross-volume Move entry may retain destination evidence only when destination commit was durable.");
                }
                if (hasDestinationEvidence)
                {
                    RequirePresent(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                }
                else
                {
                    RequireNull(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                }
                break;

            case FileCrossVolumeMoveEntryState.RecoveryRequired:
                RequirePresent(entry.Failure, nameof(entry.Failure));
                RequirePresent(entry.CompletedAtUtc, nameof(entry.CompletedAtUtc));
                RequirePresent(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));
                if (entry.DestinationCommittedAtUtc.HasValue != hasDestinationEvidence)
                {
                    throw new ArgumentException(
                        "Recovery-sensitive destination observation must be complete when recorded.");
                }
                if (entry.SourceDeleteStartedAtUtc.HasValue)
                {
                    RequireDestinationEvidence(entry, hasDestinationEvidence);
                    RequirePresent(entry.DestinationCommittedAtUtc, nameof(entry.DestinationCommittedAtUtc));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(entry.State));
        }
    }

    private static void ValidateEntryChronology(
        IReadOnlyList<FileCrossVolumeMoveActionEntry> entries)
    {
        var frontierReached = false;
        foreach (var entry in entries)
        {
            if (entry.State == FileCrossVolumeMoveEntryState.Skipped)
            {
                continue;
            }

            if (!frontierReached && entry.State == FileCrossVolumeMoveEntryState.Moved)
            {
                continue;
            }

            if (!frontierReached &&
                entry.State is FileCrossVolumeMoveEntryState.Pending or
                    FileCrossVolumeMoveEntryState.CopyMutationStarted or
                    FileCrossVolumeMoveEntryState.DestinationCommitted or
                    FileCrossVolumeMoveEntryState.SourceDeleteStarted or
                    FileCrossVolumeMoveEntryState.Failed or
                    FileCrossVolumeMoveEntryState.RecoveryRequired)
            {
                frontierReached = true;
                continue;
            }

            if (frontierReached && entry.State == FileCrossVolumeMoveEntryState.Pending)
            {
                continue;
            }

            throw new ArgumentException(
                "Cross-volume Move history must preserve a completed Moved prefix followed by at most one active/terminal frontier and then only Pending entries; Skipped entries are neutral.");
        }
    }

    private static void ValidateTerminalState(
        IReadOnlyList<FileCrossVolumeMoveActionEntry> entries,
        FileCrossVolumeMoveTerminalState? terminalState,
        DateTimeOffset? completedAtUtc)
    {
        if (terminalState.HasValue != completedAtUtc.HasValue)
        {
            throw new ArgumentException(
                "Cross-volume Move terminal state and completion timestamp must be recorded together.");
        }

        if (terminalState is null)
        {
            return;
        }

        switch (terminalState.Value)
        {
            case FileCrossVolumeMoveTerminalState.Succeeded:
                if (entries.Any(static entry =>
                    entry.State is not FileCrossVolumeMoveEntryState.Moved and
                        not FileCrossVolumeMoveEntryState.Skipped))
                {
                    throw new ArgumentException(
                        "Succeeded cross-volume Move history requires every entry to be Moved or Skipped.");
                }
                break;

            case FileCrossVolumeMoveTerminalState.Cancelled:
                if (entries.Any(static entry =>
                    entry.State is FileCrossVolumeMoveEntryState.CopyMutationStarted or
                        FileCrossVolumeMoveEntryState.SourceDeleteStarted or
                        FileCrossVolumeMoveEntryState.RecoveryRequired or
                        FileCrossVolumeMoveEntryState.Failed))
                {
                    throw new ArgumentException(
                        "Cancelled cross-volume Move history cannot contain an unresolved mutation barrier, recovery state, or failure.");
                }
                break;

            case FileCrossVolumeMoveTerminalState.Failed:
                if (!entries.Any(static entry => entry.State == FileCrossVolumeMoveEntryState.Failed) ||
                    entries.Any(static entry =>
                        entry.State is FileCrossVolumeMoveEntryState.CopyMutationStarted or
                            FileCrossVolumeMoveEntryState.SourceDeleteStarted or
                            FileCrossVolumeMoveEntryState.RecoveryRequired))
                {
                    throw new ArgumentException(
                        "Failed cross-volume Move history requires a durable safe failure and no unresolved mutation barrier.");
                }
                break;

            case FileCrossVolumeMoveTerminalState.RecoveryRequired:
                if (!entries.Any(static entry =>
                    entry.State is FileCrossVolumeMoveEntryState.CopyMutationStarted or
                        FileCrossVolumeMoveEntryState.SourceDeleteStarted or
                        FileCrossVolumeMoveEntryState.RecoveryRequired))
                {
                    throw new ArgumentException(
                        "RecoveryRequired cross-volume Move history must retain explicit recovery-sensitive entry state.");
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(terminalState));
        }
    }

    private static void RequireNoDestinationEvidence(
        FileCrossVolumeMoveActionEntry entry,
        bool hasDestinationEvidence)
    {
        if (hasDestinationEvidence || entry.DestinationCommittedAtUtc.HasValue)
        {
            throw new ArgumentException(
                "Destination commit evidence cannot exist before destination commit.");
        }
    }

    private static void RequireDestinationEvidence(
        FileCrossVolumeMoveActionEntry entry,
        bool hasDestinationEvidence)
    {
        if (!hasDestinationEvidence)
        {
            throw new ArgumentException(
                "This cross-volume Move state requires durable destination identity and content evidence.");
        }
    }

    private static void RequirePresent<T>(T? value, string name)
    {
        if (value is null)
        {
            throw new ArgumentException($"Cross-volume Move history requires {name} for this state.");
        }
    }

    private static void RequireNull<T>(T? value, string name)
    {
        if (value is not null)
        {
            throw new ArgumentException($"Cross-volume Move history requires {name} to be absent for this state.");
        }
    }
}

public interface IFileCrossVolumeMoveActionHistoryStore
{
    ValueTask<FileCrossVolumeMoveActionHistory> BeginAsync(
        FileOperationExecutionValidationResult validation,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileCrossVolumeMoveActionHistory> MarkCopyMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileCrossVolumeMoveActionHistory> CommitDestinationAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileCrossVolumeMoveActionHistory> MarkEntryFailedAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileCrossVolumeMoveActionHistory> MarkSourceDeleteStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileCrossVolumeMoveActionHistory> CommitSourceDeletedAsync(
        Guid operationId,
        int ordinal,
        FileIdentity deletedSourceIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileCrossVolumeMoveActionHistory> MarkRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity? destinationIdentity = null,
        FileContentFingerprint? destinationContentFingerprint = null,
        CancellationToken cancellationToken = default);

    ValueTask<FileCrossVolumeMoveActionHistory> CompleteAsync(
        Guid operationId,
        FileCrossVolumeMoveTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileCrossVolumeMoveActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FileCrossVolumeMoveActionHistory>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);
}