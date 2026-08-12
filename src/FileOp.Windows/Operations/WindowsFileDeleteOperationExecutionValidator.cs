using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

public sealed class WindowsFileDeleteProtectedLocationPolicy : IFileDeleteProtectedLocationPolicy
{
    private static readonly string[] RootManagedNames =
    [
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "Boot",
        "EFI",
    ];

    private readonly IReadOnlyList<string> _protectedTrees;

    public WindowsFileDeleteProtectedLocationPolicy()
        : this(GetDefaultProtectedTrees())
    {
    }

    public WindowsFileDeleteProtectedLocationPolicy(IEnumerable<string> protectedTrees)
    {
        ArgumentNullException.ThrowIfNull(protectedTrees);
        _protectedTrees = protectedTrees
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeForComparison)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public FileDeleteProtectedLocationResult Evaluate(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        string normalized;
        string root;
        try
        {
            normalized = NormalizeForComparison(canonicalPath);
            root = Path.GetPathRoot(Path.GetFullPath(canonicalPath)) ?? string.Empty;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Blocked($"The canonical path cannot be classified safely: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return Blocked("The canonical path has no resolvable filesystem root.");
        }

        var normalizedRoot = NormalizeForComparison(root);
        if (PathsEqual(normalized, normalizedRoot))
        {
            return Blocked("Filesystem volume/share roots are protected from delete authorization review.");
        }

        foreach (var protectedTree in _protectedTrees)
        {
            if (IsSameOrDescendantPath(normalized, protectedTree))
            {
                return Blocked($"The path is inside protected system-managed tree '{protectedTree}'.");
            }
        }

        foreach (var name in RootManagedNames)
        {
            var managedRoot = NormalizeForComparison(Path.Combine(root, name));
            if (IsSameOrDescendantPath(normalized, managedRoot))
            {
                return Blocked($"The path is inside protected root-managed tree '{managedRoot}'.");
            }
        }

        return new FileDeleteProtectedLocationResult(
            FileDeleteProtectedLocationDecision.AllowedForReview,
            "The canonical path is outside the protected-location trees enforced by this review boundary.");
    }

    private static IReadOnlyList<string> GetDefaultProtectedTrees()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
        };
        return folders
            .Select(Environment.GetFolderPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    private static FileDeleteProtectedLocationResult Blocked(string reason) =>
        new(FileDeleteProtectedLocationDecision.Blocked, reason);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendantPath(string candidate, string root)
    {
        var normalizedCandidate = NormalizeForComparison(candidate);
        var normalizedRoot = NormalizeForComparison(root);
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForComparison(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == Path.VolumeSeparatorChar)
        {
            normalized += Path.DirectorySeparatorChar;
        }
        return normalized;
    }
}

public sealed class WindowsFileDeleteOperationExecutionValidator : IFileDeleteOperationExecutionValidator
{
    private readonly IFileOperationCanonicalPathResolver _resolver;
    private readonly IFileDeleteProtectedLocationPolicy _protectedLocationPolicy;

    public WindowsFileDeleteOperationExecutionValidator(
        IFileOperationCanonicalPathResolver? resolver = null,
        IFileDeleteProtectedLocationPolicy? protectedLocationPolicy = null)
    {
        _resolver = resolver ?? new WindowsFileOperationCanonicalPathResolver();
        _protectedLocationPolicy = protectedLocationPolicy ?? new WindowsFileDeleteProtectedLocationPolicy();
    }

    public async ValueTask<FileDeleteOperationExecutionValidationResult> ValidateAsync(
        FileDeleteOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Intent);
        ArgumentNullException.ThrowIfNull(plan.Intent.Entries);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceDirectory = await _resolver
            .ResolveAsync(plan.Intent.SourceDirectoryPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var rootProblem = ValidateSourceDirectory(sourceDirectory);
        if (rootProblem is not null)
        {
            return BlockPlan(plan, sourceDirectory, rootProblem);
        }

        var rootPolicy = _protectedLocationPolicy.Evaluate(sourceDirectory.CanonicalPath);
        if (rootPolicy.IsBlocked)
        {
            return BlockPlan(
                plan,
                sourceDirectory,
                $"The canonical source directory is protected: {rootPolicy.Reason}");
        }

        var items = new List<FileDeleteOperationExecutionValidationItem>(plan.Intent.Entries.Count);
        foreach (var entry in plan.Intent.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ValidateEntryAsync(
                plan,
                entry,
                sourceDirectory,
                cancellationToken).ConfigureAwait(false));
        }

        var blocked = items.Count(static item =>
            item.Decision == FileDeleteOperationExecutionValidationDecision.Blocked);
        var status = blocked == 0
            ? FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview
            : FileDeleteOperationExecutionValidationStatus.Blocked;
        return new FileDeleteOperationExecutionValidationResult(
            plan,
            sourceDirectory,
            items,
            status,
            DateTimeOffset.UtcNow,
            $"Delete execution validation: {items.Count - blocked} ready for authorization review, {blocked} blocked. " +
            "Canonical source relationships and protected-location policy were checked against fresh handle-resolved evidence. " +
            "No delete authorization was granted and no filesystem mutation was attempted.");
    }

    private async ValueTask<FileDeleteOperationExecutionValidationItem> ValidateEntryAsync(
        FileDeleteOperationPlan plan,
        FileOperationEntry entry,
        FileOperationCanonicalPath sourceDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string requestedSource;
        try
        {
            requestedSource = Path.GetFullPath(entry.Path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Blocked(
                entry,
                Error(entry.Path, "InvalidSourcePath", exception.Message),
                "The captured source entry path is invalid.");
        }

        var requestedParent = Path.GetDirectoryName(requestedSource);
        if (string.IsNullOrWhiteSpace(requestedParent) ||
            !PathsEqual(requestedParent, plan.Intent.SourceDirectoryPath))
        {
            return Blocked(
                entry,
                Missing(requestedSource),
                "The captured source entry is not a direct child of the captured source directory.");
        }

        var leafName = Path.GetFileName(requestedSource);
        if (string.IsNullOrWhiteSpace(leafName) ||
            !string.Equals(leafName, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(
                entry,
                Missing(requestedSource),
                "The captured source name no longer matches its path.");
        }
        if (leafName.Contains(Path.VolumeSeparatorChar))
        {
            return Blocked(
                entry,
                Missing(requestedSource),
                "Alternate data stream names are not supported by delete execution validation.");
        }
        if (entry.IsDirectory)
        {
            return Blocked(
                entry,
                Missing(requestedSource),
                "Directory deletion remains outside the file-only delete execution-validation boundary.");
        }

        var source = await _resolver
            .ResolveAsync(requestedSource, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (source.State is FileOperationCanonicalPathState.Inaccessible or FileOperationCanonicalPathState.Error)
        {
            return Blocked(entry, source, "The source file cannot be resolved safely.");
        }
        if (source.State != FileOperationCanonicalPathState.File || source.Identity is null)
        {
            return Blocked(entry, source, "The source file is missing, changed type, or lacks stable identity evidence.");
        }
        if (source.IsLeafReparsePoint)
        {
            return Blocked(entry, source, "A source file that is itself a reparse point is not eligible for delete authorization review.");
        }

        var canonicalSourceParent = Path.GetDirectoryName(NormalizeForComparison(source.CanonicalPath));
        if (string.IsNullOrWhiteSpace(canonicalSourceParent) ||
            !PathsEqual(canonicalSourceParent, sourceDirectory.CanonicalPath))
        {
            return Blocked(
                entry,
                source,
                "The source resolves outside the canonical captured source directory.");
        }

        var policy = _protectedLocationPolicy.Evaluate(source.CanonicalPath);
        if (policy.IsBlocked)
        {
            return Blocked(entry, source, $"The canonical source file is protected: {policy.Reason}");
        }

        return new FileDeleteOperationExecutionValidationItem(
            entry,
            source,
            FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
            "The current file identity, canonical parent relationship, file-only scope and protected-location policy are consistent. Fresh user authorization and a mutation-bound identity lease are still required before deletion can be considered.");
    }

    private static string? ValidateSourceDirectory(FileOperationCanonicalPath sourceDirectory)
    {
        if (sourceDirectory.State is FileOperationCanonicalPathState.Inaccessible or FileOperationCanonicalPathState.Error)
        {
            return "The source directory cannot be resolved safely.";
        }
        if (sourceDirectory.State != FileOperationCanonicalPathState.Directory || sourceDirectory.Identity is null)
        {
            return "The source directory is missing, changed type, or lacks stable identity evidence.";
        }
        if (sourceDirectory.IsLeafReparsePoint)
        {
            return "A source directory that is itself a reparse point is not eligible for delete authorization review.";
        }
        return null;
    }

    private static FileDeleteOperationExecutionValidationResult BlockPlan(
        FileDeleteOperationPlan plan,
        FileOperationCanonicalPath sourceDirectory,
        string summary) =>
        new(
            plan,
            sourceDirectory,
            Array.Empty<FileDeleteOperationExecutionValidationItem>(),
            FileDeleteOperationExecutionValidationStatus.Blocked,
            DateTimeOffset.UtcNow,
            summary + " No delete authorization was granted and no filesystem mutation was attempted.");

    private static FileDeleteOperationExecutionValidationItem Blocked(
        FileOperationEntry entry,
        FileOperationCanonicalPath source,
        string message) =>
        new(entry, source, FileDeleteOperationExecutionValidationDecision.Blocked, message);

    private static FileOperationCanonicalPath Missing(string path) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Missing,
            IsLeafReparsePoint: false);

    private static FileOperationCanonicalPath Error(
        string path,
        string code,
        string message) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Error,
            IsLeafReparsePoint: false,
            ErrorCode: code,
            ErrorMessage: message);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForComparison(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == Path.VolumeSeparatorChar)
        {
            normalized += Path.DirectorySeparatorChar;
        }
        return normalized;
    }
}
