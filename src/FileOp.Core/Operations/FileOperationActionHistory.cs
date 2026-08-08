using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum FileOperationActionEntryState
{
    Pending,
    MutationStarted,
    Committed,
    Skipped,
    Failed,
    RecoveryRequired,
}

public enum FileOperationActionTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
    RecoveryRequired,
}

public enum FileOperationUndoKind
{
    None,
    DeleteCreatedDestination,
}

public sealed record FileOperationActionEntry(
    int Ordinal,
    FileOperationEntry Entry,
    string CanonicalSourcePath,
    string CanonicalDestinationPath,
    FileOperationActionEntryState State,
    DateTimeOffset? MutationStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    FileIdentity? SourceIdentity,
    FileIdentity? DestinationIdentity,
    FileOperationUndoKind UndoKind,
    FileOperationFailure? Failure)
{
    public bool IsUndoCandidate =>
        !Entry.IsDirectory &&
        State == FileOperationActionEntryState.Committed &&
        UndoKind == FileOperationUndoKind.DeleteCreatedDestination &&
        DestinationIdentity.HasValue;
}

public sealed record FileOperationActionHistory
{
    public FileOperationActionHistory(
        Guid operationId,
        DateTimeOffset queuedAtUtc,
        DateTimeOffset validatedAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        FileOperationKind kind,
        FileOperationCollisionPolicy collisionPolicy,
        string sourceDirectoryPath,
        string destinationDirectoryPath,
        string canonicalSourceDirectoryPath,
        string canonicalDestinationDirectoryPath,
        FileOperationActionTerminalState? terminalState,
        IEnumerable<FileOperationActionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        OperationId = operationId;
        QueuedAtUtc = queuedAtUtc;
        ValidatedAtUtc = validatedAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Kind = kind;
        CollisionPolicy = collisionPolicy;
        SourceDirectoryPath = sourceDirectoryPath;
        DestinationDirectoryPath = destinationDirectoryPath;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        CanonicalDestinationDirectoryPath = canonicalDestinationDirectoryPath;
        TerminalState = terminalState;
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    public Guid OperationId { get; }

    public DateTimeOffset QueuedAtUtc { get; }

    public DateTimeOffset ValidatedAtUtc { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public FileOperationKind Kind { get; }

    public FileOperationCollisionPolicy CollisionPolicy { get; }

    public string SourceDirectoryPath { get; }

    public string DestinationDirectoryPath { get; }

    public string CanonicalSourceDirectoryPath { get; }

    public string CanonicalDestinationDirectoryPath { get; }

    public FileOperationActionTerminalState? TerminalState { get; }

    public IReadOnlyList<FileOperationActionEntry> Entries { get; }

    public bool RequiresRecovery =>
        TerminalState == FileOperationActionTerminalState.RecoveryRequired ||
        Entries.Any(static entry =>
            entry.State is FileOperationActionEntryState.MutationStarted or
                FileOperationActionEntryState.RecoveryRequired);

    public IReadOnlyList<FileOperationActionEntry> UndoCandidateEntries =>
        Entries.Where(static entry => entry.IsUndoCandidate).ToArray();
}

public interface IFileOperationActionHistoryStore
{
    ValueTask<FileOperationActionHistory> BeginAsync(
        FileOperationExecutionValidationResult validation,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> CommitCopyAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> CompleteAsync(
        Guid operationId,
        FileOperationActionTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);
}
