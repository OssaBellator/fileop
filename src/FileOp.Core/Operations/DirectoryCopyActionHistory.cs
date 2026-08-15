using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum DirectoryCopyActionKind
{
    CreateDirectory,
    CopyFile,
}

public enum DirectoryCopyActionEntryState
{
    Pending,
    MutationStarted,
    Committed,
    Failed,
    RecoveryRequired,
}

public enum DirectoryCopyActionTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
    RecoveryRequired,
}

/// <summary>
/// One immutable recursive directory-Copy transaction. The initial reviewed manifest must
/// pass DirectoryCopyFreshManifestGate again before durable history is allowed to begin.
/// This plan is evidence only and grants no filesystem mutation authority.
/// </summary>
public sealed record DirectoryCopyTransactionPlan
{
    public DirectoryCopyTransactionPlan(
        Guid operationId,
        DateTimeOffset queuedAtUtc,
        DirectoryOperationTreeManifest reviewedManifest,
        FileOperationCanonicalPath destinationParent,
        string canonicalDestinationRootPath)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Directory Copy operation ID must be non-empty.", nameof(operationId));
        }
        ArgumentNullException.ThrowIfNull(reviewedManifest);
        ArgumentNullException.ThrowIfNull(destinationParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDestinationRootPath);
        if (destinationParent.State != FileOperationCanonicalPathState.Directory ||
            destinationParent.IsLeafReparsePoint ||
            destinationParent.Identity is not FileIdentity)
        {
            throw new ArgumentException(
                "Directory Copy destination parent requires canonical non-reparse directory identity evidence.",
                nameof(destinationParent));
        }

        var destinationRoot = NormalizePath(canonicalDestinationRootPath);
        var parent = NormalizePath(destinationParent.CanonicalPath);
        var expectedParent = Path.GetDirectoryName(destinationRoot);
        if (string.IsNullOrWhiteSpace(expectedParent) ||
            !string.Equals(parent, NormalizePath(expectedParent), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Directory Copy destination root must be an immediate child of the validated destination parent.",
                nameof(canonicalDestinationRootPath));
        }
        if (IsSameOrDescendant(parent, reviewedManifest.CanonicalRootPath) ||
            IsSameOrDescendant(destinationRoot, reviewedManifest.CanonicalRootPath))
        {
            throw new ArgumentException(
                "Directory Copy destination cannot be the reviewed source root or lie inside its subtree.",
                nameof(canonicalDestinationRootPath));
        }

        OperationId = operationId;
        QueuedAtUtc = queuedAtUtc;
        ReviewedManifest = reviewedManifest;
        DestinationParent = destinationParent;
        CanonicalDestinationRootPath = destinationRoot;
    }

    public Guid OperationId { get; }
    public DateTimeOffset QueuedAtUtc { get; }
    public DirectoryOperationTreeManifest ReviewedManifest { get; }
    public FileOperationCanonicalPath DestinationParent { get; }
    public string CanonicalDestinationRootPath { get; }
    public bool GrantsMutationAuthority => false;

    private static bool IsSameOrDescendant(string candidate, string root)
    {
        var normalizedCandidate = NormalizePath(candidate);
        var normalizedRoot = NormalizePath(root);
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
            normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
}

public sealed record DirectoryCopyActionEntry(
    int Ordinal,
    DirectoryCopyActionKind Kind,
    string RelativePath,
    string CanonicalSourcePath,
    FileIdentity SourceIdentity,
    string CanonicalDestinationPath,
    int? DestinationParentOrdinal,
    FileIdentity? DestinationIdentity,
    FileContentFingerprint? DestinationContentFingerprint,
    DirectoryCopyActionEntryState State,
    DateTimeOffset? MutationStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    FileOperationFailure? Failure)
{
    public bool GrantsReplayAuthority => false;
    public bool GrantsRollbackAuthority => false;
}

public sealed record DirectoryCopyActionHistory
{
    public DirectoryCopyActionHistory(
        Guid operationId,
        DateTimeOffset queuedAtUtc,
        DateTimeOffset freshValidatedAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string canonicalSourceRootPath,
        FileIdentity sourceRootIdentity,
        string canonicalDestinationParentPath,
        FileIdentity destinationParentIdentity,
        string canonicalDestinationRootPath,
        DirectoryCopyActionTerminalState? terminalState,
        IEnumerable<DirectoryCopyActionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = entries.OrderBy(static entry => entry.Ordinal).ToArray();
        if (operationId == Guid.Empty || snapshot.Length == 0)
        {
            throw new ArgumentException("Directory Copy history requires a non-empty operation and action list.");
        }
        for (var index = 0; index < snapshot.Length; index++)
        {
            var entry = snapshot[index];
            if (entry.Ordinal != index)
            {
                throw new ArgumentException("Directory Copy history action ordinals must be contiguous and zero-based.", nameof(entries));
            }
            if (entry.DestinationParentOrdinal is int parentOrdinal &&
                (parentOrdinal < 0 || parentOrdinal >= index ||
                 snapshot[parentOrdinal].Kind != DirectoryCopyActionKind.CreateDirectory))
            {
                throw new ArgumentException(
                    "Directory Copy child actions must reference an earlier destination-directory creation action.",
                    nameof(entries));
            }
            if (entry.Kind == DirectoryCopyActionKind.CreateDirectory && entry.DestinationContentFingerprint is not null)
            {
                throw new ArgumentException("Directory creation history cannot contain file-content fingerprint evidence.", nameof(entries));
            }
            if (entry.Kind == DirectoryCopyActionKind.CopyFile &&
                entry.State == DirectoryCopyActionEntryState.Committed &&
                entry.DestinationContentFingerprint is null)
            {
                throw new ArgumentException("Committed directory Copy file history requires destination SHA-256 evidence.", nameof(entries));
            }
        }

        OperationId = operationId;
        QueuedAtUtc = queuedAtUtc;
        FreshValidatedAtUtc = freshValidatedAtUtc;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        CanonicalSourceRootPath = canonicalSourceRootPath;
        SourceRootIdentity = sourceRootIdentity;
        CanonicalDestinationParentPath = canonicalDestinationParentPath;
        DestinationParentIdentity = destinationParentIdentity;
        CanonicalDestinationRootPath = canonicalDestinationRootPath;
        TerminalState = terminalState;
        Entries = Array.AsReadOnly(snapshot);
    }

    public Guid OperationId { get; }
    public DateTimeOffset QueuedAtUtc { get; }
    public DateTimeOffset FreshValidatedAtUtc { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public string CanonicalSourceRootPath { get; }
    public FileIdentity SourceRootIdentity { get; }
    public string CanonicalDestinationParentPath { get; }
    public FileIdentity DestinationParentIdentity { get; }
    public string CanonicalDestinationRootPath { get; }
    public DirectoryCopyActionTerminalState? TerminalState { get; }
    public IReadOnlyList<DirectoryCopyActionEntry> Entries { get; }

    public bool RequiresRecovery =>
        TerminalState == DirectoryCopyActionTerminalState.RecoveryRequired ||
        Entries.Any(static entry =>
            entry.State is DirectoryCopyActionEntryState.MutationStarted or
                DirectoryCopyActionEntryState.RecoveryRequired);

    public bool GrantsAutomaticReplayAuthority => false;
    public bool GrantsRollbackAuthority => false;
    public bool GrantsDeleteAuthority => false;
}

public interface IDirectoryCopyActionHistoryStore
{
    ValueTask<DirectoryCopyActionHistory> BeginAsync(
        DirectoryCopyTransactionPlan plan,
        DirectoryCopyFreshManifestGateResult freshGate,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectoryCopyActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectoryCopyActionHistory> CommitAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint? destinationContentFingerprint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectoryCopyActionHistory> MarkFailedBeforeMutationAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectoryCopyActionHistory> MarkRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity? observedDestinationIdentity = null,
        FileContentFingerprint? observedDestinationContentFingerprint = null,
        CancellationToken cancellationToken = default);

    ValueTask<DirectoryCopyActionHistory> CompleteAsync(
        Guid operationId,
        DirectoryCopyActionTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<DirectoryCopyActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}
