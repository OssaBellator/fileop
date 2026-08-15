using System;
using System.IO;
using System.Linq;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DirectoryOperationTreeManifestRevalidationTests
{
    private const ulong Volume = 91;

    [TestMethod]
    public void EquivalentFreshManifestHasNoChangesAndNoAuthority()
    {
        var root = RootPath();
        var initial = Manifest(root, 1,
            Entry(root, "folder", 2, DirectoryOperationTreeEntryKind.Directory),
            Entry(root, Path.Combine("folder", "file.txt"), 3, DirectoryOperationTreeEntryKind.File));
        var fresh = Manifest(root, 1,
            Entry(root, Path.Combine("folder", "file.txt"), 3, DirectoryOperationTreeEntryKind.File),
            Entry(root, "folder", 2, DirectoryOperationTreeEntryKind.Directory));

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsTrue(result.EvidenceStillMatches);
        Assert.AreEqual(0, result.Changes.Count);
        Assert.AreSame(initial, result.Initial);
        Assert.AreSame(fresh, result.Fresh);
        Assert.IsFalse(result.GrantsMutationAuthority);
        Assert.IsFalse(result.GrantsCopyAuthority);
        Assert.IsFalse(result.GrantsCreateAuthority);
        Assert.IsFalse(result.GrantsDeleteAuthority);
    }

    [TestMethod]
    public void RootIdentityChangeFailsClosed()
    {
        var root = RootPath();
        var initial = Manifest(root, 1);
        var fresh = Manifest(root, 99);

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsFalse(result.EvidenceStillMatches);
        AssertChange(result, DirectoryOperationTreeManifestChangeKind.RootIdentityChanged, relativePath: null);
    }

    [TestMethod]
    public void RootCanonicalPathChangeFailsClosed()
    {
        var initial = Manifest(RootPath(), 1);
        var fresh = Manifest(Path.Combine(RootPath(), "replacement-root"), 1);

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsFalse(result.EvidenceStillMatches);
        AssertChange(result, DirectoryOperationTreeManifestChangeKind.RootCanonicalPathChanged, relativePath: null);
    }

    [TestMethod]
    public void AddedEntryFailsClosed()
    {
        var root = RootPath();
        var initial = Manifest(root, 1);
        var fresh = Manifest(root, 1,
            Entry(root, "new.txt", 2, DirectoryOperationTreeEntryKind.File));

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsFalse(result.EvidenceStillMatches);
        AssertChange(result, DirectoryOperationTreeManifestChangeKind.EntryAdded, "new.txt");
    }

    [TestMethod]
    public void RemovedEntryFailsClosed()
    {
        var root = RootPath();
        var initial = Manifest(root, 1,
            Entry(root, "old.txt", 2, DirectoryOperationTreeEntryKind.File));
        var fresh = Manifest(root, 1);

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsFalse(result.EvidenceStillMatches);
        AssertChange(result, DirectoryOperationTreeManifestChangeKind.EntryRemoved, "old.txt");
    }

    [TestMethod]
    public void ReplacedObjectIdentityFailsClosed()
    {
        var root = RootPath();
        var initial = Manifest(root, 1,
            Entry(root, "same-name.txt", 2, DirectoryOperationTreeEntryKind.File));
        var fresh = Manifest(root, 1,
            Entry(root, "same-name.txt", 200, DirectoryOperationTreeEntryKind.File));

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsFalse(result.EvidenceStillMatches);
        AssertChange(
            result,
            DirectoryOperationTreeManifestChangeKind.EntryIdentityChanged,
            "same-name.txt");
    }

    [TestMethod]
    public void FileDirectoryKindChangeFailsClosed()
    {
        var root = RootPath();
        var initial = Manifest(root, 1,
            Entry(root, "node", 2, DirectoryOperationTreeEntryKind.File));
        var fresh = Manifest(root, 1,
            Entry(root, "node", 2, DirectoryOperationTreeEntryKind.Directory));

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsFalse(result.EvidenceStillMatches);
        AssertChange(result, DirectoryOperationTreeManifestChangeKind.EntryKindChanged, "node");
    }

    [TestMethod]
    public void CurrentCaseInsensitiveNamespaceModelAcceptsCaseOnlyPathSpellingChange()
    {
        var root = RootPath();
        var initial = Manifest(root, 1,
            Entry(root, "Folder", 2, DirectoryOperationTreeEntryKind.Directory),
            Entry(root, Path.Combine("Folder", "File.TXT"), 3, DirectoryOperationTreeEntryKind.File));
        var fresh = Manifest(root, 1,
            Entry(root, "folder", 2, DirectoryOperationTreeEntryKind.Directory),
            Entry(root, Path.Combine("folder", "file.txt"), 3, DirectoryOperationTreeEntryKind.File));

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsTrue(result.EvidenceStillMatches);
        Assert.AreEqual(0, result.Changes.Count);
    }

    [TestMethod]
    public void MultipleChangesAreReportedDeterministicallyWithoutAuthority()
    {
        var root = RootPath();
        var initial = Manifest(root, 1,
            Entry(root, "b.txt", 2, DirectoryOperationTreeEntryKind.File),
            Entry(root, "a.txt", 3, DirectoryOperationTreeEntryKind.File));
        var fresh = Manifest(root, 99,
            Entry(root, "a.txt", 300, DirectoryOperationTreeEntryKind.File),
            Entry(root, "c.txt", 4, DirectoryOperationTreeEntryKind.File));

        var result = DirectoryOperationTreeManifestRevalidator.Compare(initial, fresh);

        Assert.IsFalse(result.EvidenceStillMatches);
        CollectionAssert.AreEqual(
            new[]
            {
                DirectoryOperationTreeManifestChangeKind.RootIdentityChanged,
                DirectoryOperationTreeManifestChangeKind.EntryIdentityChanged,
                DirectoryOperationTreeManifestChangeKind.EntryRemoved,
                DirectoryOperationTreeManifestChangeKind.EntryAdded,
            },
            result.Changes.Select(static change => change.Kind).ToArray());
        CollectionAssert.AreEqual(
            new string?[] { null, "a.txt", "b.txt", "c.txt" },
            result.Changes.Select(static change => change.RelativePath).ToArray());
        Assert.IsFalse(result.GrantsMutationAuthority);
    }

    private static void AssertChange(
        DirectoryOperationTreeManifestRevalidationResult result,
        DirectoryOperationTreeManifestChangeKind kind,
        string? relativePath)
    {
        Assert.IsTrue(result.Changes.Any(change =>
            change.Kind == kind &&
            string.Equals(change.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(result.GrantsMutationAuthority);
    }

    private static DirectoryOperationTreeManifest Manifest(
        string root,
        ulong rootFileReference,
        params DirectoryOperationTreeEntryEvidence[] entries) =>
        DirectoryOperationTreeManifest.Create(
            new DirectoryOperationFidelityEvidence(
                root,
                DirectoryOperationFidelityFeature.None,
                EnumerationComplete: true,
                MetadataInspectionComplete: true),
            new FileOperationCanonicalPath(
                root,
                root,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(Volume, rootFileReference)),
            entries);

    private static DirectoryOperationTreeEntryEvidence Entry(
        string root,
        string relativePath,
        ulong fileReference,
        DirectoryOperationTreeEntryKind kind) =>
        new(
            relativePath,
            new FileOperationCanonicalPath(
                Path.Combine(root, relativePath),
                Path.Combine(root, relativePath),
                kind == DirectoryOperationTreeEntryKind.Directory
                    ? FileOperationCanonicalPathState.Directory
                    : FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(Volume, fileReference)));

    private static string RootPath() =>
        Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FileOp.DirectoryOperationTreeManifest.Revalidation.Tests",
            "root"));
}
