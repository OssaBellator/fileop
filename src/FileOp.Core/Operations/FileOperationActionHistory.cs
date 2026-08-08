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
    public bool IsUndoEligible =>
        State == FileOperationActionEntryState.Committed &&
        UndoKind == FileOperationUndoKind.DeleteCreatedDestination &&
        DestinationIdentity.HasValue;
}

public sealed record FileOperationActionHistory(
    Guid OperationId,
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset ValidatedAtUtc,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    FileOperationKind Kind,
    FileOperationCollisionPolicy CollisionPolicy,
    string SourceDirectoryPath,
    string DestinationDirectoryPath,
    string CanonicalSourceDirectoryPath,
    string CanonicalDestinationDirectoryPath,
    FileOperationActionTerminalState? TerminalState,
    IReadOnlyList<FileOperationActionEntry> Entries)
{
    public bool RequiresRecovery =>
        TerminalState == FileOperationActionTerminalState.RecoveryRequired ||
        Entries.Any(static entry =>
            entry.State is FileOperationActionEntryState.MutationStarted or
                FileOperationActionEntryState.RecoveryRequired);

    public IReadOnlyList<FileOperationActionEntry> UndoEligibleEntries =>
        Entries.Where(static entry => entry.IsUndoEligible).ToArray();
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
