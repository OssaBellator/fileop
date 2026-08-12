using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum FileDeleteOperationActionEntryState
{
    Pending,
    MutationStarted,
    Committed,
    Failed,
    RecoveryRequired,
}

public enum FileDeleteOperationActionTerminalState
{
    Succeeded,
    Failed,
    RecoveryRequired,
}

public sealed record FileDeleteOperationActionEntry(
    int Ordinal,
    FileOperationEntry Entry,
    string CanonicalSourcePath,
    FileIdentity SourceIdentity,
    FileDeleteOperationActionEntryState State,
    DateTimeOffset? MutationStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    FileOperationFailure? Failure);

public sealed class FileDeleteOperationActionHistory
{
    public FileDeleteOperationActionHistory(
        Guid operationId,
        Guid authorizationId,
        DateTimeOffset queuedAtUtc,
        DateTimeOffset validatedAtUtc,
        DateTimeOffset authorizedAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string sourcePaneId,
        Guid sourceTabId,
        string canonicalSourceDirectoryPath,
        FileIdentity sourceDirectoryIdentity,
        FileDeleteOperationActionTerminalState? terminalState,
        IEnumerable<FileDeleteOperationActionEntry> entries)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Delete action-history operation ID cannot be empty.", nameof(operationId));
        }
        if (authorizationId == Guid.Empty)
        {
            throw new ArgumentException("Delete action-history authorization ID cannot be empty.", nameof(authorizationId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePaneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentNullException.ThrowIfNull(entries);
        if (terminalState.HasValue != completedAtUtc.HasValue)
        {
            throw new ArgumentException(
                "Delete action-history terminal state and completion time must appear together.",
                nameof(terminalState));
        }

        var snapshot = entries.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("Delete action history must contain at least one file entry.", nameof(entries));
        }
        for (var index = 0; index < snapshot.Length; index++)
        {
            ValidateEntry(snapshot[index], index);
        }

        ValidateTerminalShape(snapshot, terminalState);

        OperationId = operationId;
        AuthorizationId = authorizationId;
        QueuedAtUtc = queuedAtUtc.ToUniversalTime();
        ValidatedAtUtc = validatedAtUtc.ToUniversalTime();
        AuthorizedAtUtc = authorizedAtUtc.ToUniversalTime();
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAtUtc?.ToUniversalTime();
        SourcePaneId = sourcePaneId;
        SourceTabId = sourceTabId;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        TerminalState = terminalState;
        Entries = Array.AsReadOnly(snapshot);
    }

    public Guid OperationId { get; }

    public Guid AuthorizationId { get; }

    public DateTimeOffset QueuedAtUtc { get; }

    public DateTimeOffset ValidatedAtUtc { get; }

    public DateTimeOffset AuthorizedAtUtc { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public string SourcePaneId { get; }

    public Guid SourceTabId { get; }

    public string CanonicalSourceDirectoryPath { get; }

    public FileIdentity SourceDirectoryIdentity { get; }

    public FileDeleteOperationActionTerminalState? TerminalState { get; }

    public IReadOnlyList<FileDeleteOperationActionEntry> Entries { get; }

    public bool IsTerminal => TerminalState.HasValue;

    public bool IsRecoverySensitive =>
        TerminalState == FileDeleteOperationActionTerminalState.RecoveryRequired ||
        Entries.Any(static entry =>
            entry.State is FileDeleteOperationActionEntryState.MutationStarted or
                FileDeleteOperationActionEntryState.RecoveryRequired);

    public bool DeleteMutationAuthorized => false;

    private static void ValidateEntry(FileDeleteOperationActionEntry entry, int expectedOrdinal)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Entry.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Entry.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.CanonicalSourcePath);
        if (entry.Ordinal != expectedOrdinal || entry.Entry.IsDirectory || !Enum.IsDefined(entry.State))
        {
            throw new ArgumentException("Delete action-history entries must be ordered file-only evidence.", nameof(entry));
        }
        if (entry.Failure is { } failure)
        {
            ValidateFailure(failure);
        }

        var mutationStarted = entry.MutationStartedAtUtc.HasValue;
        var completed = entry.CompletedAtUtc.HasValue;
        var failurePresent = entry.Failure is not null;
        var valid = entry.State switch
        {
            FileDeleteOperationActionEntryState.Pending => !mutationStarted && !completed && !failurePresent,
            FileDeleteOperationActionEntryState.MutationStarted => mutationStarted && !completed && !failurePresent,
            FileDeleteOperationActionEntryState.Committed => mutationStarted && completed && !failurePresent,
            FileDeleteOperationActionEntryState.Failed => !mutationStarted && completed && failurePresent,
            FileDeleteOperationActionEntryState.RecoveryRequired => mutationStarted && completed && failurePresent,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"Delete action-history entry {entry.Ordinal} has timestamps/failure data inconsistent with state {entry.State}.",
                nameof(entry));
        }
    }

    private static void ValidateTerminalShape(
        IReadOnlyList<FileDeleteOperationActionEntry> entries,
        FileDeleteOperationActionTerminalState? terminalState)
    {
        var hasRecovery = entries.Any(static entry =>
            entry.State is FileDeleteOperationActionEntryState.MutationStarted or
                FileDeleteOperationActionEntryState.RecoveryRequired);
        if (!terminalState.HasValue)
        {
            return;
        }

        switch (terminalState.Value)
        {
            case FileDeleteOperationActionTerminalState.Succeeded:
                if (entries.Any(static entry => entry.State != FileDeleteOperationActionEntryState.Committed))
                {
                    throw new ArgumentException("Successful delete action history requires every entry to be committed.", nameof(terminalState));
                }
                break;

            case FileDeleteOperationActionTerminalState.Failed:
                if (hasRecovery)
                {
                    throw new ArgumentException("Failed delete action history cannot contain a recovery-sensitive entry.", nameof(terminalState));
                }
                if (!entries.Any(static entry => entry.State == FileDeleteOperationActionEntryState.Failed))
                {
                    throw new ArgumentException("Failed delete action history requires at least one failed entry.", nameof(terminalState));
                }
                break;

            case FileDeleteOperationActionTerminalState.RecoveryRequired:
                if (!hasRecovery)
                {
                    throw new ArgumentException("RecoveryRequired delete action history needs a recovery-sensitive entry.", nameof(terminalState));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(terminalState));
        }
    }

    internal static void ValidateFailure(FileOperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Message);
    }
}

public interface IFileDeleteOperationActionHistoryStore : IAsyncDisposable
{
    ValueTask<FileDeleteOperationActionHistory> BeginAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        CancellationToken cancellationToken = default);

    ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        CancellationToken cancellationToken = default);

    ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(
        Guid operationId,
        int ordinal,
        FileIdentity deletedSourceIdentity,
        CancellationToken cancellationToken = default);

    ValueTask<FileDeleteOperationActionHistory> MarkFailedAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        CancellationToken cancellationToken = default);

    ValueTask<FileDeleteOperationActionHistory> MarkMutationRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        CancellationToken cancellationToken = default);

    ValueTask<FileDeleteOperationActionHistory> CompleteAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    ValueTask<FileDeleteOperationActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}
