using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum DirectorySameVolumeMoveActionEntryState
{
    Pending,
    MutationStarted,
    Committed,
    Skipped,
    Failed,
    RecoveryRequired,
}

public enum DirectorySameVolumeMoveActionTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
    RecoveryRequired,
}

public sealed record DirectorySameVolumeMoveActionEntry(
    int Ordinal,
    FileOperationEntry Entry,
    string CanonicalSourcePath,
    string CanonicalDestinationPath,
    FileIdentity SourceIdentity,
    FileIdentity? DestinationIdentity,
    DirectorySameVolumeMoveActionEntryState State,
    DateTimeOffset? MutationStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    FileOperationFailure? Failure)
{
    public bool GrantsReplayAuthority => false;

    public bool GrantsRollbackAuthority => false;
}

/// <summary>
/// Durable history for same-volume directory rename is intentionally separate from
/// FileOperationActionHistory schema v1, whose mutation states are regular-file-only.
/// History records evidence and recovery state; it never authorizes replay or rollback.
/// </summary>
public sealed record DirectorySameVolumeMoveActionHistory
{
    public DirectorySameVolumeMoveActionHistory(
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
        DirectorySameVolumeMoveActionTerminalState? terminalState,
        IEnumerable<DirectorySameVolumeMoveActionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Directory Move operation ID must be non-empty.", nameof(operationId));
        }

        if (sourceDirectoryIdentity == destinationDirectoryIdentity)
        {
            throw new ArgumentException(
                "Directory Move source and destination roots cannot be the same identity.");
        }

        if (sourceDirectoryIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Directory Move history represents same-volume rename only; root volume serials must match.");
        }

        var snapshot = entries.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("Directory Move history requires at least one entry.", nameof(entries));
        }

        for (var ordinal = 0; ordinal < snapshot.Length; ordinal++)
        {
            var entry = snapshot[ordinal];
            if (entry.Ordinal != ordinal || !entry.Entry.IsDirectory)
            {
                throw new ArgumentException(
                    "Directory Move history requires ordered directory-only entries.",
                    nameof(entries));
            }

            if (entry.SourceIdentity.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber)
            {
                throw new ArgumentException(
                    "Directory Move source entries must remain bound to the source root volume.",
                    nameof(entries));
            }

            if (entry.DestinationIdentity is FileIdentity destinationIdentity &&
                destinationIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber)
            {
                throw new ArgumentException(
                    "Directory Move destination evidence must remain bound to the destination root volume.",
                    nameof(entries));
            }
        }

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

    public DirectorySameVolumeMoveActionTerminalState? TerminalState { get; }

    public IReadOnlyList<DirectorySameVolumeMoveActionEntry> Entries { get; }

    public bool RequiresRecovery =>
        TerminalState == DirectorySameVolumeMoveActionTerminalState.RecoveryRequired ||
        Entries.Any(static entry =>
            entry.State is DirectorySameVolumeMoveActionEntryState.MutationStarted or
                DirectorySameVolumeMoveActionEntryState.RecoveryRequired);

    public bool GrantsAutomaticReplayAuthority => false;

    public bool GrantsRollbackAuthority => false;
}

public interface IDirectorySameVolumeMoveActionHistoryStore
{
    ValueTask<DirectorySameVolumeMoveActionHistory> BeginAsync(
        FileOperationExecutionValidationResult validation,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectorySameVolumeMoveActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectorySameVolumeMoveActionHistory> MarkEntryFailedBeforeMutationAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectorySameVolumeMoveActionHistory> CommitAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectorySameVolumeMoveActionHistory> MarkRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity? observedDestinationIdentity = null,
        CancellationToken cancellationToken = default);

    ValueTask<DirectorySameVolumeMoveActionHistory> CompleteAsync(
        Guid operationId,
        DirectorySameVolumeMoveActionTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectorySameVolumeMoveActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}
