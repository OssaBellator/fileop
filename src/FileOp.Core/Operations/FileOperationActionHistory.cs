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
    FileOperationFailure? Failure,
    FileContentFingerprint? DestinationContentFingerprint = null)
{
    /// <summary>
    /// Optional durable observation of the destination object's hard-link count
    /// after Copy. Null means legacy or otherwise unavailable topology evidence.
    /// </summary>
    public uint? DestinationHardLinkCount { get; init; }

    public bool IsUndoCandidate =>
        !Entry.IsDirectory &&
        State == FileOperationActionEntryState.Committed &&
        UndoKind == FileOperationUndoKind.DeleteCreatedDestination &&
        DestinationIdentity.HasValue;
}

public sealed record FileOperationActionHistory
{
    public FileOperationActionHistory(
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
        IEnumerable<FileOperationActionEntry> Entries,
        FileIdentity? SourceDirectoryIdentity = null,
        FileIdentity? DestinationDirectoryIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(Entries);
        if (SourceDirectoryIdentity.HasValue != DestinationDirectoryIdentity.HasValue)
        {
            throw new ArgumentException(
                "Action-history root identity evidence must contain both source and destination identities, or neither.");
        }

        var entrySnapshot = Entries.ToArray();
        if (entrySnapshot.Any(entry =>
            entry.State != FileOperationActionEntryState.Skipped &&
            (Kind != FileOperationKind.Copy || entry.Entry.IsDirectory)))
        {
            throw new ArgumentException(
                "Action-history schema v1 supports mutation state only for Copy files; directory or non-Copy entries may be recorded only as skipped.",
                nameof(Entries));
        }

        this.OperationId = OperationId;
        this.QueuedAtUtc = QueuedAtUtc;
        this.ValidatedAtUtc = ValidatedAtUtc;
        this.StartedAtUtc = StartedAtUtc;
        this.CompletedAtUtc = CompletedAtUtc;
        this.Kind = Kind;
        this.CollisionPolicy = CollisionPolicy;
        this.SourceDirectoryPath = SourceDirectoryPath;
        this.DestinationDirectoryPath = DestinationDirectoryPath;
        this.CanonicalSourceDirectoryPath = CanonicalSourceDirectoryPath;
        this.CanonicalDestinationDirectoryPath = CanonicalDestinationDirectoryPath;
        this.TerminalState = TerminalState;
        this.Entries = Array.AsReadOnly(entrySnapshot);
        this.SourceDirectoryIdentity = SourceDirectoryIdentity;
        this.DestinationDirectoryIdentity = DestinationDirectoryIdentity;
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

    public FileIdentity? SourceDirectoryIdentity { get; }

    public FileIdentity? DestinationDirectoryIdentity { get; }

    public bool HasVerifiedRootIdentities =>
        SourceDirectoryIdentity.HasValue && DestinationDirectoryIdentity.HasValue;

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
        FileContentFingerprint destinationContentFingerprint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default,
        FileIdentity? destinationIdentity = null,
        FileContentFingerprint? destinationContentFingerprint = null);

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

/// <summary>
/// Optional stronger persistence capability for destination topology evidence.
/// Existing IFileOperationActionHistoryStore implementers remain source-compatible.
/// Windows topology-aware composition wraps this capability so the unchanged Copy
/// executor's commit/recovery calls cannot silently lose a verified count.
/// </summary>
public interface IFileOperationActionHistoryHardLinkEvidenceStore : IFileOperationActionHistoryStore
{
    ValueTask<FileOperationActionHistory> CommitCopyWithHardLinkEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        uint destinationHardLinkCount,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        uint destinationHardLinkCount,
        CancellationToken cancellationToken = default);
}
