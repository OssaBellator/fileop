using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DirectoryOperationTreeManifestTests
{
    private const ulong Volume = 77;

    [TestMethod]
    public void EmptyPlainDirectoryCreatesNonAuthorizingManifest()
    {
        var root = RootPath();
        var rootIdentity = new FileIdentity(Volume, 1);

        var manifest = DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            rootIdentity,
            Array.Empty<DirectoryOperationTreeEntryEvidence>());

        Assert.AreEqual(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), manifest.CanonicalRootPath);
        Assert.AreEqual(rootIdentity, manifest.RootIdentity);
        Assert.AreEqual(0, manifest.EntryCount);
        Assert.AreEqual(0, manifest.DirectoryCount);
        Assert.AreEqual(0, manifest.FileCount);
        Assert.IsFalse(manifest.GrantsMutationAuthority);
    }

    [TestMethod]
    public void NestedPlainTreeIsDeterministicallyOrderedParentBeforeChild()
    {
        var root = RootPath();
        var manifest = DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                Entry(root, Path.Combine("z", "child.txt"), 6, DirectoryOperationTreeEntryKind.File),
                Entry(root, "root.txt", 2, DirectoryOperationTreeEntryKind.File),
                Entry(root, "z", 5, DirectoryOperationTreeEntryKind.Directory),
                Entry(root, Path.Combine("a", "nested"), 4, DirectoryOperationTreeEntryKind.Directory),
                Entry(root, "a", 3, DirectoryOperationTreeEntryKind.Directory),
                Entry(root, Path.Combine("a", "nested", "leaf.txt"), 7, DirectoryOperationTreeEntryKind.File),
            });

        CollectionAssert.AreEqual(
            new[]
            {
                "a",
                "z",
                "root.txt",
                Path.Combine("a", "nested"),
                Path.Combine("z", "child.txt"),
                Path.Combine("a", "nested", "leaf.txt"),
            },
            manifest.Entries.Select(static entry => entry.RelativePath).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 1, 1, 2, 2, 3 },
            manifest.Entries.Select(static entry => entry.Depth).ToArray());
        Assert.AreEqual(3, manifest.DirectoryCount);
        Assert.AreEqual(3, manifest.FileCount);
        Assert.IsFalse(manifest.GrantsMutationAuthority);
    }

    [TestMethod]
    public void UnsupportedFidelityCannotCreateManifest()
    {
        var root = RootPath();
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            new DirectoryOperationFidelityEvidence(
                root,
                DirectoryOperationFidelityFeature.AlternateDataStreams,
                EnumerationComplete: true,
                MetadataInspectionComplete: true),
            new FileIdentity(Volume, 1),
            Array.Empty<DirectoryOperationTreeEntryEvidence>()));

        Assert.AreEqual(typeof(NotSupportedException), exception.GetType());
        StringAssert.Contains(exception.Message, "complete plain-tree fidelity evidence");
    }

    [TestMethod]
    public void IncompleteFidelityCannotCreateManifest()
    {
        var root = RootPath();
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            new DirectoryOperationFidelityEvidence(
                root,
                DirectoryOperationFidelityFeature.None,
                EnumerationComplete: false,
                MetadataInspectionComplete: true),
            new FileIdentity(Volume, 1),
            Array.Empty<DirectoryOperationTreeEntryEvidence>()));

        Assert.AreEqual(typeof(NotSupportedException), exception.GetType());
        StringAssert.Contains(exception.Message, "complete plain-tree fidelity evidence");
    }

    [TestMethod]
    public void RootedRelativePathIsRejected()
    {
        var root = RootPath();
        var rooted = Path.Combine(Path.GetPathRoot(root)!, "outside.txt");
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                new DirectoryOperationTreeEntryEvidence(
                    rooted,
                    rooted,
                    new FileIdentity(Volume, 2),
                    DirectoryOperationTreeEntryKind.File),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "must be relative");
    }

    [TestMethod]
    public void ParentDirectoryEscapeIsRejected()
    {
        var root = RootPath();
        var relative = Path.Combine("..", "escape.txt");
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                new DirectoryOperationTreeEntryEvidence(
                    relative,
                    Path.GetFullPath(Path.Combine(root, relative)),
                    new FileIdentity(Volume, 2),
                    DirectoryOperationTreeEntryKind.File),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "parent-directory segment");
    }

    [TestMethod]
    public void CanonicalPathMismatchIsRejected()
    {
        var root = RootPath();
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                new DirectoryOperationTreeEntryEvidence(
                    "expected.txt",
                    Path.Combine(root, "different.txt"),
                    new FileIdentity(Volume, 2),
                    DirectoryOperationTreeEntryKind.File),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "expected canonical path");
    }

    [TestMethod]
    public void DuplicateCaseInsensitiveRelativePathIsRejected()
    {
        var root = RootPath();
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                Entry(root, "Alpha.txt", 2, DirectoryOperationTreeEntryKind.File),
                Entry(root, "alpha.TXT", 3, DirectoryOperationTreeEntryKind.File),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "duplicate relative path");
    }

    [TestMethod]
    public void DifferentVolumeDescendantIdentityIsRejected()
    {
        var root = RootPath();
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                new DirectoryOperationTreeEntryEvidence(
                    "child.txt",
                    Path.Combine(root, "child.txt"),
                    new FileIdentity(Volume + 1, 2),
                    DirectoryOperationTreeEntryKind.File),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "different filesystem volume");
    }

    [TestMethod]
    public void MissingParentDirectoryIsRejected()
    {
        var root = RootPath();
        var relative = Path.Combine("missing", "child.txt");
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                Entry(root, relative, 2, DirectoryOperationTreeEntryKind.File),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "missing parent directory");
    }

    [TestMethod]
    public void ParentRepresentedAsFileIsRejected()
    {
        var root = RootPath();
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                Entry(root, "parent", 2, DirectoryOperationTreeEntryKind.File),
                Entry(root, Path.Combine("parent", "child.txt"), 3, DirectoryOperationTreeEntryKind.File),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "represented as a file");
    }

    [TestMethod]
    public void ReusedObjectIdentityIsRejectedAsContradictoryPlainTreeEvidence()
    {
        var root = RootPath();
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                Entry(root, "one.txt", 2, DirectoryOperationTreeEntryKind.File),
                Entry(root, "two.txt", 2, DirectoryOperationTreeEntryKind.File),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "reuses a filesystem identity");
        StringAssert.Contains(exception.Message, "Hard-link/cycle evidence is unsupported");
    }

    [TestMethod]
    public void RootIdentityCannotReappearAsDescendant()
    {
        var root = RootPath();
        var exception = CaptureException(() => DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileIdentity(Volume, 1),
            new[]
            {
                Entry(root, "cycle", 1, DirectoryOperationTreeEntryKind.Directory),
            }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "reuses a filesystem identity");
    }

    [TestMethod]
    public void ManifestCopiesInputEvidenceAndPreservesExactIdentityBinding()
    {
        var root = RootPath();
        var source = new List<DirectoryOperationTreeEntryEvidence>
        {
            Entry(root, "folder", 2, DirectoryOperationTreeEntryKind.Directory),
            Entry(root, Path.Combine("folder", "file.txt"), 3, DirectoryOperationTreeEntryKind.File),
        };
        var rootIdentity = new FileIdentity(Volume, 1);

        var manifest = DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            rootIdentity,
            source);
        source.Clear();

        Assert.AreEqual(2, manifest.EntryCount);
        Assert.AreEqual(rootIdentity, manifest.RootIdentity);
        Assert.AreEqual(new FileIdentity(Volume, 2), manifest.Entries[0].Identity);
        Assert.AreEqual(new FileIdentity(Volume, 3), manifest.Entries[1].Identity);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(root, "folder")), manifest.Entries[0].CanonicalPath);
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(root, "folder", "file.txt")),
            manifest.Entries[1].CanonicalPath);
        Assert.IsFalse(manifest.GrantsMutationAuthority);
    }

    private static DirectoryOperationFidelityEvidence PlainFidelity(string root) =>
        new(
            root,
            DirectoryOperationFidelityFeature.None,
            EnumerationComplete: true,
            MetadataInspectionComplete: true);

    private static DirectoryOperationTreeEntryEvidence Entry(
        string root,
        string relativePath,
        ulong fileReference,
        DirectoryOperationTreeEntryKind kind) =>
        new(
            relativePath,
            Path.Combine(root, relativePath),
            new FileIdentity(Volume, fileReference),
            kind);

    private static string RootPath() =>
        Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FileOp.DirectoryOperationTreeManifest.Tests",
            "root"));

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        Assert.Fail("Expected operation to throw.");
        throw new InvalidOperationException("Assert.Fail returned unexpectedly.");
    }
}
