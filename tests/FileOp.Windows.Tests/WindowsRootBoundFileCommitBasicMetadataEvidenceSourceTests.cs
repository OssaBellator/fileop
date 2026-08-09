using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsRootBoundFileCommitBasicMetadataEvidenceSourceTests
{
    [TestMethod]
    public async Task SourceCapturesPositiveCountAndStableBasicMetadata()
    {
        using var fixture = new EvidenceFixture();
        File.SetCreationTimeUtc(fixture.Path, new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(fixture.Path, new DateTime(2021, 2, 3, 4, 5, 8, DateTimeKind.Utc));
        File.SetAttributes(fixture.Path, FileAttributes.Archive | FileAttributes.Hidden);
        var history = await fixture.CreateHistoryAsync();

        var evidence = await new WindowsRootBoundFileCommitBasicMetadataEvidenceSource()
            .ReadVerifiedEvidenceAsync(history, 0, fixture.DestinationIdentity!.Value);

        Assert.IsTrue(evidence.HardLinkCount > 0);
        Assert.AreEqual(
            unchecked((ulong)File.GetCreationTimeUtc(fixture.Path).ToFileTimeUtc()),
            evidence.BasicMetadata.CreationTimeFileTime);
        Assert.AreEqual(
            unchecked((ulong)File.GetLastWriteTimeUtc(fixture.Path).ToFileTimeUtc()),
            evidence.BasicMetadata.LastWriteTimeFileTime);
        Assert.AreEqual(
            (uint)(FileAttributes.Archive | FileAttributes.Hidden),
            evidence.BasicMetadata.StableCopiedAttributes);
    }

    [TestMethod]
    public async Task ExistingTrustedWriterCanCoexistWithCommitMetadataRead()
    {
        using var fixture = new EvidenceFixture();
        var history = await fixture.CreateHistoryAsync();
        await using var writer = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite);

        var evidence = await new WindowsRootBoundFileCommitBasicMetadataEvidenceSource()
            .ReadVerifiedEvidenceAsync(history, 0, fixture.DestinationIdentity!.Value);

        Assert.IsTrue(evidence.HardLinkCount > 0);
    }

    [TestMethod]
    public async Task WrongDestinationIdentityFailsClosed()
    {
        using var fixture = new EvidenceFixture();
        var history = await fixture.CreateHistoryAsync();
        var wrong = new FileIdentity(
            fixture.DestinationIdentity!.Value.VolumeSerialNumber,
            fixture.DestinationIdentity.Value.FileReferenceNumber + 1);

        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await new WindowsRootBoundFileCommitBasicMetadataEvidenceSource()
                .ReadVerifiedEvidenceAsync(history, 0, wrong));
    }

    [TestMethod]
    public async Task ReplacedRootWithSameFileMovedBackFailsClosed()
    {
        using var fixture = new EvidenceFixture();
        var history = await fixture.CreateHistoryAsync();
        var originalRoot = fixture.Root + ".original";
        Directory.Move(fixture.Root, originalRoot);
        Directory.CreateDirectory(fixture.Root);
        File.Move(System.IO.Path.Combine(originalRoot, "payload.bin"), fixture.Path);
        fixture.AddCleanupRoot(originalRoot);

        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await new WindowsRootBoundFileCommitBasicMetadataEvidenceSource()
                .ReadVerifiedEvidenceAsync(
                    history,
                    0,
                    fixture.DestinationIdentity!.Value));
    }

    private sealed class EvidenceFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileOp.BasicMetadata.CommitEvidence.Tests",
            Guid.NewGuid().ToString("N"));
        private string? _additionalCleanupRoot;

        public EvidenceFixture()
        {
            Directory.CreateDirectory(_root);
            Root = _root;
            Path = System.IO.Path.Combine(Root, "payload.bin");
            File.WriteAllText(Path, "content");
        }

        public string Root { get; }
        public string Path { get; }
        public FileIdentity? DestinationIdentity { get; private set; }
        public void AddCleanupRoot(string path) => _additionalCleanupRoot = path;

        public async Task<FileOperationActionHistory> CreateHistoryAsync()
        {
            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var root = await resolver.ResolveAsync(Root);
            var leaf = await resolver.ResolveAsync(Path);
            Assert.AreEqual(FileOperationCanonicalPathState.Directory, root.State);
            Assert.AreEqual(FileOperationCanonicalPathState.File, leaf.State);
            Assert.IsNotNull(root.Identity);
            Assert.IsNotNull(leaf.Identity);
            DestinationIdentity = leaf.Identity;
            var now = DateTimeOffset.UtcNow;
            var entry = new FileOperationActionEntry(
                0,
                new FileOperationEntry(@"C:\Source\payload.bin", "payload.bin", IsDirectory: false),
                @"C:\Real\Source\payload.bin",
                leaf.CanonicalPath,
                FileOperationActionEntryState.MutationStarted,
                now,
                CompletedAtUtc: null,
                new FileIdentity(1, 10),
                DestinationIdentity: null,
                FileOperationUndoKind.None,
                Failure: null);
            return new FileOperationActionHistory(
                Guid.NewGuid(),
                now,
                now,
                now,
                CompletedAtUtc: null,
                FileOperationKind.Copy,
                FileOperationCollisionPolicy.Stop,
                @"C:\Source",
                Root,
                @"C:\Real\Source",
                root.CanonicalPath,
                TerminalState: null,
                new[] { entry },
                SourceDirectoryIdentity: new FileIdentity(2, 20),
                DestinationDirectoryIdentity: root.Identity);
        }

        public void Dispose()
        {
            foreach (var root in new[] { Root, _additionalCleanupRoot })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(Path))
                    {
                        File.SetAttributes(Path, FileAttributes.Normal);
                    }
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
