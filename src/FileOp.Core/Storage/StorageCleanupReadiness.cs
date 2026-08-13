using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Core.Storage;

public enum StorageCleanupReadinessStatus
{
    CurrentEvidenceConsistent,
    CandidateChanged,
    Blocked,
    Unavailable,
}

public sealed record StorageCleanupCurrentFileEvidence(
    string RequestedPath,
    string CanonicalPath,
    FileIdentity Identity,
    long LogicalBytes,
    long AllocatedBytes,
    uint HardLinkCount,
    DateTimeOffset LastWriteTimeUtc,
    bool IsLeafReparsePoint);

public sealed record StorageCleanupReadinessPreview(
    string Path,
    string RuleId,
    StorageCleanupReadinessStatus Status,
    DateTimeOffset CheckedAtUtc,
    string? CanonicalPath,
    FileIdentity? CurrentIdentity,
    long? CurrentLogicalBytes,
    long? CurrentAllocatedBytes,
    uint? CurrentHardLinkCount,
    DateTimeOffset? CurrentLastWriteTimeUtc,
    string Detail)
{
    public bool CanonicalEvidencePassed =>
        Status == StorageCleanupReadinessStatus.CurrentEvidenceConsistent;

    public long CurrentPhysicalReleaseUpperBoundBytes =>
        Status == StorageCleanupReadinessStatus.CurrentEvidenceConsistent &&
        CurrentHardLinkCount == 1 &&
        CurrentAllocatedBytes is { } allocatedBytes &&
        allocatedBytes >= 0
            ? allocatedBytes
            : 0;

    // Readiness evidence is deliberately separate from the reviewed Files delete session.
    // It can never be reused as deletion consent or mutation authority.
    public bool CleanupMutationAuthorized => false;
}

public static class StorageCleanupReadinessAnalyzer
{
    public static StorageCleanupReadinessPreview Analyze(
        StorageReviewCandidate candidate,
        string reviewRoot,
        FileOperationCanonicalPath canonicalReviewRoot,
        FileOperationCanonicalPath canonicalCandidate,
        StorageCleanupCurrentFileEvidence? currentFile,
        string? currentFileError,
        DateTimeOffset? checkedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewRoot);
        ArgumentNullException.ThrowIfNull(canonicalReviewRoot);
        ArgumentNullException.ThrowIfNull(canonicalCandidate);

        var checkedAt = (checkedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        StorageCleanupReadinessPreview Result(
            StorageCleanupReadinessStatus status,
            string detail,
            StorageCleanupCurrentFileEvidence? evidence = null) =>
            new(
                candidate.Path,
                candidate.RuleId,
                status,
                checkedAt,
                evidence?.CanonicalPath ??
                    (string.IsNullOrWhiteSpace(canonicalCandidate.CanonicalPath)
                        ? null
                        : canonicalCandidate.CanonicalPath),
                evidence?.Identity ?? canonicalCandidate.Identity,
                evidence?.LogicalBytes,
                evidence?.AllocatedBytes,
                evidence?.HardLinkCount,
                evidence?.LastWriteTimeUtc,
                detail);

        if (!PathsEqual(canonicalReviewRoot.RequestedPath, reviewRoot) ||
            !PathsEqual(canonicalCandidate.RequestedPath, candidate.Path))
        {
            return Result(
                StorageCleanupReadinessStatus.Blocked,
                "Canonical cleanup-readiness evidence is not bound to the requested known-location root and candidate paths.");
        }

        if (canonicalReviewRoot.State is FileOperationCanonicalPathState.Inaccessible or
            FileOperationCanonicalPathState.Error)
        {
            return Result(
                StorageCleanupReadinessStatus.Unavailable,
                "The known-location root could not be resolved through the current canonical Files path boundary. Cleanup remains unavailable.");
        }
        if (canonicalReviewRoot.State != FileOperationCanonicalPathState.Directory)
        {
            return Result(
                StorageCleanupReadinessStatus.CandidateChanged,
                "The reviewed known-location root is missing or is no longer a directory. Refresh Optimize before using this evidence.");
        }
        if (canonicalReviewRoot.IsLeafReparsePoint)
        {
            return Result(
                StorageCleanupReadinessStatus.Blocked,
                "The reviewed known-location root is itself a reparse point. FileOp will not advance this candidate toward cleanup through an unresolved redirect boundary.");
        }

        if (canonicalCandidate.State is FileOperationCanonicalPathState.Inaccessible or
            FileOperationCanonicalPathState.Error)
        {
            return Result(
                StorageCleanupReadinessStatus.Unavailable,
                "The review candidate cannot currently be resolved through the canonical Files path boundary. Cleanup remains unavailable.");
        }
        if (canonicalCandidate.State != FileOperationCanonicalPathState.File)
        {
            return Result(
                StorageCleanupReadinessStatus.CandidateChanged,
                "The review candidate is missing or is no longer a regular file. Refresh Optimize before using this evidence.");
        }
        if (canonicalCandidate.IsLeafReparsePoint)
        {
            return Result(
                StorageCleanupReadinessStatus.Blocked,
                "The review candidate is itself a reparse point. FileOp will not treat redirected content as cleanup-ready evidence.");
        }
        if (canonicalReviewRoot.Identity is null || canonicalCandidate.Identity is null)
        {
            return Result(
                StorageCleanupReadinessStatus.Unavailable,
                "Current filesystem identity evidence is unavailable for the review root or candidate.");
        }
        if (canonicalReviewRoot.Identity.Value.VolumeSerialNumber !=
            canonicalCandidate.Identity.Value.VolumeSerialNumber)
        {
            return Result(
                StorageCleanupReadinessStatus.Blocked,
                "The candidate resolves to a different filesystem volume than its reviewed known-location root.");
        }
        if (!IsPathWithinRoot(canonicalCandidate.CanonicalPath, canonicalReviewRoot.CanonicalPath))
        {
            return Result(
                StorageCleanupReadinessStatus.Blocked,
                "The candidate resolves outside the canonical known-location root. FileOp will not advance redirected path evidence toward cleanup.");
        }

        if (currentFile is null)
        {
            return Result(
                StorageCleanupReadinessStatus.Unavailable,
                string.IsNullOrWhiteSpace(currentFileError)
                    ? "Current read-only file metadata could not be captured."
                    : currentFileError);
        }
        if (!PathsEqual(currentFile.RequestedPath, candidate.Path))
        {
            return Result(
                StorageCleanupReadinessStatus.Blocked,
                "The current metadata snapshot is not bound to the requested review candidate path.",
                currentFile);
        }
        if (currentFile.IsLeafReparsePoint)
        {
            return Result(
                StorageCleanupReadinessStatus.Blocked,
                "The current file handle reports a reparse-point leaf. Cleanup remains blocked.",
                currentFile);
        }
        if (currentFile.HardLinkCount == 0 || currentFile.AllocatedBytes < 0)
        {
            return Result(
                StorageCleanupReadinessStatus.Unavailable,
                "Current hard-link or allocation evidence is invalid, so physical release evidence cannot be trusted.",
                currentFile);
        }
        if (!PathsEqual(currentFile.CanonicalPath, canonicalCandidate.CanonicalPath) ||
            currentFile.Identity != canonicalCandidate.Identity.Value)
        {
            return Result(
                StorageCleanupReadinessStatus.CandidateChanged,
                "The file identity or final path changed between canonical validation and the current metadata snapshot. Refresh and re-check before using this evidence.",
                currentFile);
        }
        if (currentFile.LogicalBytes != candidate.LogicalBytes ||
            currentFile.LastWriteTimeUtc.UtcDateTime.Ticks !=
                candidate.LastWriteTime.ToUniversalTime().UtcDateTime.Ticks)
        {
            return Result(
                StorageCleanupReadinessStatus.CandidateChanged,
                "The current file size or last-write time no longer matches the indexed review evidence. Refresh Optimize before considering cleanup.",
                currentFile);
        }

        var releaseDetail = currentFile.HardLinkCount == 1
            ? "The current file has one hard link, so its current allocated bytes are a per-path physical-release upper bound if a later authorized deletion actually removes this file object. "
            : $"The current file has {currentFile.HardLinkCount:N0} hard links, so deleting this one path alone is not evidence that its allocation would be released. ";
        return Result(
            StorageCleanupReadinessStatus.CurrentEvidenceConsistent,
            "Current canonical path, file type, identity snapshot, logical size, allocation, hard-link count and last-write time are internally consistent with this indexed review candidate. " +
            releaseDetail +
            "The indexed review did not preserve a prior physical identity, so continuity of the same file object since indexing is not proven. " +
            "This readiness preview does not authorize deletion. If permanent deletion is later chosen, the file must be selected in Files and independently pass the Files recovery, preflight, canonical validation, explicit confirmation, durable history and mutation-authorization boundary.",
            currentFile);
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
