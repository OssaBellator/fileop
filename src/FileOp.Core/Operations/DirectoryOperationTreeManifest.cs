using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum DirectoryOperationTreeEntryKind
{
    Directory,
    File,
}

/// <summary>
/// One relative descendant plus the canonical execution-grade evidence that identified it.
/// The manifest copies the exact canonical path/type/identity fields and never retains this
/// caller-owned evidence object as mutation authority.
/// </summary>
public sealed record DirectoryOperationTreeEntryEvidence(
    string RelativePath,
    FileOperationCanonicalPath Source);

public sealed record DirectoryOperationTreeManifestEntry(
    string RelativePath,
    string CanonicalPath,
    FileIdentity Identity,
    DirectoryOperationTreeEntryKind Kind,
    int Depth);

/// <summary>
/// Immutable topology evidence for a future recursive directory Copy executor.
///
/// Construction requires the existing fidelity classifier to accept a complete plain tree,
/// then binds every descendant to one execution-validated canonical root, one filesystem
/// volume, one unique object identity and a deterministic parent-before-child relative-path
/// topology. The manifest is evidence only: it deliberately grants no mutation authority and
/// performs no filesystem enumeration or mutation itself.
/// </summary>
public sealed class DirectoryOperationTreeManifest
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private DirectoryOperationTreeManifest(
        string canonicalRootPath,
        FileIdentity rootIdentity,
        IReadOnlyList<DirectoryOperationTreeManifestEntry> entries)
    {
        CanonicalRootPath = canonicalRootPath;
        RootIdentity = rootIdentity;
        Entries = entries;
    }

    public string CanonicalRootPath { get; }

    public FileIdentity RootIdentity { get; }

    public IReadOnlyList<DirectoryOperationTreeManifestEntry> Entries { get; }

    public int EntryCount => Entries.Count;

    public int DirectoryCount => Entries.Count(static entry =>
        entry.Kind == DirectoryOperationTreeEntryKind.Directory);

    public int FileCount => Entries.Count(static entry =>
        entry.Kind == DirectoryOperationTreeEntryKind.File);

    public bool GrantsMutationAuthority => false;

    public static DirectoryOperationTreeManifest Create(
        DirectoryOperationFidelityEvidence fidelityEvidence,
        FileOperationCanonicalPath root,
        IEnumerable<DirectoryOperationTreeEntryEvidence> entries)
    {
        ArgumentNullException.ThrowIfNull(fidelityEvidence);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(entries);

        var support = DirectoryOperationFidelityClassifier.Classify(fidelityEvidence);
        if (!support.CanEnterFutureMutationBoundary)
        {
            throw new NotSupportedException(
                "Directory tree manifest creation requires complete plain-tree fidelity evidence: " +
                support.Summary);
        }

        var (canonicalRootPath, rootIdentity) = ValidateRoot(fidelityEvidence, root);
        var normalized = entries
            .Select(entry => NormalizeEntry(canonicalRootPath, rootIdentity, entry))
            .ToArray();

        EnsureUniqueRelativePaths(normalized);
        EnsureUniqueObjectIdentities(rootIdentity, normalized);
        EnsureCompleteParentTopology(normalized);

        var ordered = normalized
            .OrderBy(static entry => entry.Depth)
            .ThenBy(static entry => entry.Kind == DirectoryOperationTreeEntryKind.Directory ? 0 : 1)
            .ThenBy(static entry => entry.RelativePath, PathComparer)
            .ThenBy(static entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return new DirectoryOperationTreeManifest(
            canonicalRootPath,
            rootIdentity,
            Array.AsReadOnly(ordered));
    }

    private static (string CanonicalRootPath, FileIdentity RootIdentity) ValidateRoot(
        DirectoryOperationFidelityEvidence fidelityEvidence,
        FileOperationCanonicalPath root)
    {
        if (root.State != FileOperationCanonicalPathState.Directory ||
            root.IsLeafReparsePoint ||
            root.Identity is not FileIdentity rootIdentity ||
            string.IsNullOrWhiteSpace(root.CanonicalPath))
        {
            throw new ArgumentException(
                "Directory tree manifest requires a canonical non-reparse directory root with stable filesystem identity.",
                nameof(root));
        }

        var fidelityRoot = NormalizeCanonicalPath(fidelityEvidence.CanonicalRootPath);
        var executionRoot = NormalizeCanonicalPath(root.CanonicalPath);
        if (!string.Equals(fidelityRoot, executionRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Directory tree manifest fidelity evidence is not bound to the execution-validated canonical root.",
                nameof(root));
        }

        return (executionRoot, rootIdentity);
    }

    private static DirectoryOperationTreeManifestEntry NormalizeEntry(
        string canonicalRootPath,
        FileIdentity rootIdentity,
        DirectoryOperationTreeEntryEvidence entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Source);
        if (string.IsNullOrWhiteSpace(entry.RelativePath))
        {
            throw new ArgumentException(
                "Directory tree manifest descendant relative paths must be non-empty.",
                nameof(entry));
        }
        if (Path.IsPathRooted(entry.RelativePath))
        {
            throw new ArgumentException(
                $"Directory tree manifest entry '{entry.RelativePath}' must be relative to the manifest root.",
                nameof(entry));
        }

        var segments = entry.RelativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.None);
        if (segments.Length == 0 ||
            segments.Any(static segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment == "." ||
                segment == ".." ||
                segment.Contains(Path.VolumeSeparatorChar) ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.')))
        {
            throw new ArgumentException(
                $"Directory tree manifest entry '{entry.RelativePath}' contains an empty, ambiguous, stream-like, current-directory or parent-directory segment.",
                nameof(entry));
        }

        var source = entry.Source;
        var kind = source.State switch
        {
            FileOperationCanonicalPathState.Directory => DirectoryOperationTreeEntryKind.Directory,
            FileOperationCanonicalPathState.File => DirectoryOperationTreeEntryKind.File,
            _ => throw new ArgumentException(
                $"Directory tree manifest entry '{entry.RelativePath}' must resolve to an existing file or directory.",
                nameof(entry)),
        };
        if (source.IsLeafReparsePoint ||
            source.Identity is not FileIdentity identity ||
            string.IsNullOrWhiteSpace(source.CanonicalPath))
        {
            throw new ArgumentException(
                $"Directory tree manifest entry '{entry.RelativePath}' requires non-reparse canonical evidence with stable filesystem identity.",
                nameof(entry));
        }

        var normalizedRelativePath = string.Join(Path.DirectorySeparatorChar, segments);
        var expectedCanonicalPath = Path.GetFullPath(
            Path.Combine(canonicalRootPath, normalizedRelativePath));
        EnsureContainedByRoot(canonicalRootPath, expectedCanonicalPath, normalizedRelativePath);

        var observedCanonicalPath = NormalizeCanonicalPath(source.CanonicalPath);
        if (!string.Equals(
                expectedCanonicalPath,
                observedCanonicalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Directory tree manifest entry '{normalizedRelativePath}' is not bound to its expected canonical path under '{canonicalRootPath}'.",
                nameof(entry));
        }

        if (identity.VolumeSerialNumber != rootIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                $"Directory tree manifest entry '{normalizedRelativePath}' is on a different filesystem volume than the manifest root.",
                nameof(entry));
        }

        return new DirectoryOperationTreeManifestEntry(
            normalizedRelativePath,
            observedCanonicalPath,
            identity,
            kind,
            segments.Length);
    }

    private static string NormalizeCanonicalPath(string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            throw new ArgumentException("Canonical path evidence must not be empty.", nameof(canonicalPath));
        }

        var full = Path.GetFullPath(canonicalPath);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    private static void EnsureContainedByRoot(
        string canonicalRootPath,
        string candidatePath,
        string relativePath)
    {
        var rootWithSeparator = canonicalRootPath.EndsWith(Path.DirectorySeparatorChar) ||
            canonicalRootPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? canonicalRootPath
            : canonicalRootPath + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Directory tree manifest entry '{relativePath}' escapes canonical root '{canonicalRootPath}'.",
                nameof(relativePath));
        }
    }

    private static void EnsureUniqueRelativePaths(
        IReadOnlyList<DirectoryOperationTreeManifestEntry> entries)
    {
        var seen = new HashSet<string>(PathComparer);
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.RelativePath))
            {
                throw new ArgumentException(
                    $"Directory tree manifest contains duplicate relative path '{entry.RelativePath}' under the current case-insensitive namespace model.",
                    nameof(entries));
            }
        }
    }

    private static void EnsureUniqueObjectIdentities(
        FileIdentity rootIdentity,
        IReadOnlyList<DirectoryOperationTreeManifestEntry> entries)
    {
        var seen = new HashSet<FileIdentity> { rootIdentity };
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.Identity))
            {
                throw new ArgumentException(
                    $"Directory tree manifest entry '{entry.RelativePath}' reuses a filesystem identity already present in the plain-tree manifest. Hard-link/cycle evidence is unsupported.",
                    nameof(entries));
            }
        }
    }

    private static void EnsureCompleteParentTopology(
        IReadOnlyList<DirectoryOperationTreeManifestEntry> entries)
    {
        var byPath = entries.ToDictionary(
            static entry => entry.RelativePath,
            PathComparer);
        foreach (var entry in entries)
        {
            var parent = Path.GetDirectoryName(entry.RelativePath);
            if (string.IsNullOrEmpty(parent))
            {
                continue;
            }

            if (!byPath.TryGetValue(parent, out var parentEntry))
            {
                throw new ArgumentException(
                    $"Directory tree manifest entry '{entry.RelativePath}' is missing parent directory '{parent}'.",
                    nameof(entries));
            }
            if (parentEntry.Kind != DirectoryOperationTreeEntryKind.Directory)
            {
                throw new ArgumentException(
                    $"Directory tree manifest entry '{entry.RelativePath}' has parent '{parent}' represented as a file.",
                    nameof(entries));
            }
        }
    }
}
