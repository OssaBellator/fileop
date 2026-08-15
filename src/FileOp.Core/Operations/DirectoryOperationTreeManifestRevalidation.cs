using System;
using System.Collections.Generic;
using System.Linq;

namespace FileOp.Core.Operations;

public enum DirectoryOperationTreeManifestChangeKind
{
    RootCanonicalPathChanged,
    RootIdentityChanged,
    EntryAdded,
    EntryRemoved,
    EntryIdentityChanged,
    EntryKindChanged,
    EntryCanonicalPathChanged,
}

public sealed record DirectoryOperationTreeManifestChange(
    DirectoryOperationTreeManifestChangeKind Kind,
    string? RelativePath,
    string Summary);

/// <summary>
/// Immutable comparison evidence between an originally reviewed directory tree manifest
/// and a freshly acquired manifest. This result never grants filesystem mutation authority.
/// </summary>
public sealed class DirectoryOperationTreeManifestRevalidationResult
{
    internal DirectoryOperationTreeManifestRevalidationResult(
        DirectoryOperationTreeManifest initial,
        DirectoryOperationTreeManifest fresh,
        IEnumerable<DirectoryOperationTreeManifestChange> changes)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(changes);

        Initial = initial;
        Fresh = fresh;
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    public DirectoryOperationTreeManifest Initial { get; }

    public DirectoryOperationTreeManifest Fresh { get; }

    public IReadOnlyList<DirectoryOperationTreeManifestChange> Changes { get; }

    public bool EvidenceStillMatches => Changes.Count == 0;

    public bool GrantsMutationAuthority => false;

    public bool GrantsCopyAuthority => false;

    public bool GrantsCreateAuthority => false;

    public bool GrantsDeleteAuthority => false;
}

/// <summary>
/// Compares two already-validated immutable manifests. No filesystem enumeration, probing,
/// copying, creation, deletion or mutation occurs here. A future recursive executor must
/// acquire a fresh manifest separately and fail closed when this comparison reports change.
/// </summary>
public static class DirectoryOperationTreeManifestRevalidator
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static DirectoryOperationTreeManifestRevalidationResult Compare(
        DirectoryOperationTreeManifest initial,
        DirectoryOperationTreeManifest fresh)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(fresh);

        var changes = new List<DirectoryOperationTreeManifestChange>();

        if (!PathComparer.Equals(initial.CanonicalRootPath, fresh.CanonicalRootPath))
        {
            changes.Add(new DirectoryOperationTreeManifestChange(
                DirectoryOperationTreeManifestChangeKind.RootCanonicalPathChanged,
                RelativePath: null,
                $"Directory tree canonical root changed from '{initial.CanonicalRootPath}' to '{fresh.CanonicalRootPath}'."));
        }

        if (initial.RootIdentity != fresh.RootIdentity)
        {
            changes.Add(new DirectoryOperationTreeManifestChange(
                DirectoryOperationTreeManifestChangeKind.RootIdentityChanged,
                RelativePath: null,
                "Directory tree root filesystem identity changed."));
        }

        var initialByPath = initial.Entries.ToDictionary(
            static entry => entry.RelativePath,
            PathComparer);
        var freshByPath = fresh.Entries.ToDictionary(
            static entry => entry.RelativePath,
            PathComparer);

        foreach (var initialEntry in initial.Entries)
        {
            if (!freshByPath.TryGetValue(initialEntry.RelativePath, out var freshEntry))
            {
                changes.Add(new DirectoryOperationTreeManifestChange(
                    DirectoryOperationTreeManifestChangeKind.EntryRemoved,
                    initialEntry.RelativePath,
                    $"Directory tree entry '{initialEntry.RelativePath}' is no longer present."));
                continue;
            }

            if (initialEntry.Identity != freshEntry.Identity)
            {
                changes.Add(new DirectoryOperationTreeManifestChange(
                    DirectoryOperationTreeManifestChangeKind.EntryIdentityChanged,
                    initialEntry.RelativePath,
                    $"Directory tree entry '{initialEntry.RelativePath}' now resolves to a different filesystem identity."));
            }

            if (initialEntry.Kind != freshEntry.Kind)
            {
                changes.Add(new DirectoryOperationTreeManifestChange(
                    DirectoryOperationTreeManifestChangeKind.EntryKindChanged,
                    initialEntry.RelativePath,
                    $"Directory tree entry '{initialEntry.RelativePath}' changed file/directory kind."));
            }

            if (!PathComparer.Equals(initialEntry.CanonicalPath, freshEntry.CanonicalPath))
            {
                changes.Add(new DirectoryOperationTreeManifestChange(
                    DirectoryOperationTreeManifestChangeKind.EntryCanonicalPathChanged,
                    initialEntry.RelativePath,
                    $"Directory tree entry '{initialEntry.RelativePath}' changed canonical path binding."));
            }
        }

        foreach (var freshEntry in fresh.Entries)
        {
            if (!initialByPath.ContainsKey(freshEntry.RelativePath))
            {
                changes.Add(new DirectoryOperationTreeManifestChange(
                    DirectoryOperationTreeManifestChangeKind.EntryAdded,
                    freshEntry.RelativePath,
                    $"Directory tree entry '{freshEntry.RelativePath}' was added after the original manifest was reviewed."));
            }
        }

        var ordered = changes
            .OrderBy(static change => change.RelativePath is null ? 0 : 1)
            .ThenBy(static change => change.RelativePath, PathComparer)
            .ThenBy(static change => change.RelativePath, StringComparer.Ordinal)
            .ThenBy(static change => change.Kind)
            .ToArray();

        return new DirectoryOperationTreeManifestRevalidationResult(
            initial,
            fresh,
            ordered);
    }
}
