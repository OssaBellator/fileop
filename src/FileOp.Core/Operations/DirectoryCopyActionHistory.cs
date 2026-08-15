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
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDestinationParentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDestinationRootPath);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Directory Copy history requires a non-empty operation ID.", nameof(operationId));
        }
        if (terminalState.HasValue != completedAtUtc.HasValue)
        {
            throw new ArgumentException("Directory Copy terminal state and completion time must appear together.");
        }
        if (terminalState is { } terminal && !Enum.IsDefined(terminal))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        }

        var snapshot = entries.OrderBy(static entry => entry.Ordinal).ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("Directory Copy history requires at least one action.", nameof(entries));
        }
        for (var index = 0; index < snapshot.Length; index++)
        {
            ValidateEntry(
                snapshot,
                index,
                canonicalSourceRootPath,
                sourceRootIdentity,
                canonicalDestinationParentPath,
                destinationParentIdentity,
                canonicalDestinationRootPath);
        }
        ValidateEntryOrdering(snapshot);
        ValidateTerminal(snapshot, terminalState);

        OperationId = operationId;
        QueuedAtUtc = queuedAtUtc.ToUniversalTime();
        FreshValidatedAtUtc = freshValidatedAtUtc.ToUniversalTime();
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAtUtc?.ToUniversalTime();
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

    internal static void ValidateEntry(
        IReadOnlyList<DirectoryCopyActionEntry> entries,
        int index,
        string canonicalSourceRootPath,
        FileIdentity sourceRootIdentity,
        string canonicalDestinationParentPath,
        FileIdentity destinationParentIdentity,
        string canonicalDestinationRootPath)
    {
        var entry = entries[index];
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.CanonicalSourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.CanonicalDestinationPath);
        if (entry.Ordinal != index || !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.State))
        {
            throw new ArgumentException("Directory Copy history actions must have contiguous ordinals and defined kind/state values.", nameof(entries));
        }
        if (entry.SourceIdentity.VolumeSerialNumber != sourceRootIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException("Directory Copy source action identity is not bound to the source-root volume.", nameof(entries));
        }
        if (entry.DestinationIdentity is FileIdentity destinationIdentity &&
            destinationIdentity.VolumeSerialNumber != destinationParentIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException("Directory Copy destination action identity is not bound to the destination-parent volume.", nameof(entries));
        }
        if (entry.Failure is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Failure.Code);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Failure.Message);
        }

        if (index == 0)
        {
            if (entry.Kind != DirectoryCopyActionKind.CreateDirectory ||
                entry.RelativePath.Length != 0 ||
                entry.DestinationParentOrdinal is not null ||
                !PathEquals(entry.CanonicalSourcePath, canonicalSourceRootPath) ||
                !PathEquals(entry.CanonicalDestinationPath, canonicalDestinationRootPath) ||
                entry.SourceIdentity != sourceRootIdentity)
            {
                throw new ArgumentException("Directory Copy history root action is not bound to the reviewed source/destination roots.", nameof(entries));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.RelativePath);
            if (entry.DestinationParentOrdinal is not int parentOrdinal ||
                parentOrdinal < 0 || parentOrdinal >= index ||
                entries[parentOrdinal].Kind != DirectoryCopyActionKind.CreateDirectory ||
                !PathEquals(
                    Path.GetDirectoryName(entry.CanonicalDestinationPath)
                        ?? throw new ArgumentException("Directory Copy child action has no destination parent path.", nameof(entries)),
                    entries[parentOrdinal].CanonicalDestinationPath))
            {
                throw new ArgumentException(
                    "Directory Copy child actions must reference their exact earlier destination-directory creation action.",
                    nameof(entries));
            }
        }

        var started = entry.MutationStartedAtUtc.HasValue;
        var completed = entry.CompletedAtUtc.HasValue;
        var destination = entry.DestinationIdentity.HasValue;
        var fingerprint = entry.DestinationContentFingerprint is not null;
        var failure = entry.Failure is not null;
        var valid = entry.State switch
        {
            DirectoryCopyActionEntryState.Pending => !started && !completed && !destination && !fingerprint && !failure,
            DirectoryCopyActionEntryState.MutationStarted => started && !completed && !destination && !fingerprint && !failure,
            DirectoryCopyActionEntryState.Committed => started && completed && destination && !failure &&
                (entry.Kind == DirectoryCopyActionKind.CopyFile ? fingerprint : !fingerprint),
            DirectoryCopyActionEntryState.Failed => !started && completed && !destination && !fingerprint && failure,
            DirectoryCopyActionEntryState.RecoveryRequired => started && completed && failure &&
                (entry.Kind == DirectoryCopyActionKind.CreateDirectory ? !fingerprint : !fingerprint || destination),
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"Directory Copy action {entry.Ordinal} has invalid evidence for state {entry.State}.", nameof(entries));
        }
    }

    internal static void ValidateEntryOrdering(IReadOnlyList<DirectoryCopyActionEntry> entries)
    {
        var frontierSeen = false;
        var pendingSeen = false;
        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case DirectoryCopyActionEntryState.Committed:
                    if (frontierSeen || pendingSeen)
                    {
                        throw new ArgumentException("Directory Copy history must contain one contiguous committed prefix.", nameof(entries));
                    }
                    break;

                case DirectoryCopyActionEntryState.Pending:
                    pendingSeen = true;
                    break;

                case DirectoryCopyActionEntryState.MutationStarted:
                case DirectoryCopyActionEntryState.Failed:
                case DirectoryCopyActionEntryState.RecoveryRequired:
                    if (frontierSeen || pendingSeen)
                    {
                        throw new ArgumentException("Directory Copy history may contain at most one action frontier immediately after the committed prefix.", nameof(entries));
                    }
                    frontierSeen = true;
                    break;

                default:
                    throw new ArgumentException(
                        $"Directory Copy history action {entry.Ordinal} has an undefined state.",
                        nameof(entries));
            }
        }
    }

    internal static void ValidateTerminal(
        IReadOnlyList<DirectoryCopyActionEntry> entries,
        DirectoryCopyActionTerminalState? terminalState)
    {
        if (!terminalState.HasValue)
        {
            return;
        }

        switch (terminalState.Value)
        {
            case DirectoryCopyActionTerminalState.Succeeded:
                if (entries.Any(static entry => entry.State != DirectoryCopyActionEntryState.Committed))
                {
                    throw new ArgumentException("Successful directory Copy history requires every recursive action committed.", nameof(terminalState));
                }
                break;

            case DirectoryCopyActionTerminalState.Failed:
                if (entries.Count(static entry => entry.State == DirectoryCopyActionEntryState.Failed) != 1 ||
                    entries.Any(static entry => entry.State is DirectoryCopyActionEntryState.MutationStarted or DirectoryCopyActionEntryState.RecoveryRequired))
                {
                    throw new ArgumentException("Failed directory Copy history requires exactly one definite pre-mutation failure frontier and no recovery-sensitive action.", nameof(terminalState));
                }
                break;

            case DirectoryCopyActionTerminalState.Cancelled:
                if (!entries.Any(static entry => entry.State == DirectoryCopyActionEntryState.Pending) ||
                    entries.Any(static entry => entry.State is DirectoryCopyActionEntryState.MutationStarted or DirectoryCopyActionEntryState.Failed or DirectoryCopyActionEntryState.RecoveryRequired))
                {
                    throw new ArgumentException("Cancelled directory Copy history requires a committed prefix followed by at least one pending action.", nameof(terminalState));
                }
                break;

            case DirectoryCopyActionTerminalState.RecoveryRequired:
                if (entries.Count(static entry => entry.State is DirectoryCopyActionEntryState.MutationStarted or DirectoryCopyActionEntryState.RecoveryRequired) != 1 ||
                    entries.Any(static entry => entry.State == DirectoryCopyActionEntryState.Failed))
                {
                    throw new ArgumentException("RecoveryRequired directory Copy history requires exactly one recovery-sensitive frontier and no definite-failure frontier.", nameof(terminalState));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(terminalState));
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
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
