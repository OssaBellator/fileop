using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsRootBoundFileBasicMetadataEvidenceReaderTests
{
    [TestMethod]
    public async Task ReaderObservesCreationLastWriteAndSafeAttributes()
    {
        using var fixture = new ReaderFixture();
        File.SetCreationTimeUtc(fixture.Path, new DateTime(2020, 3, 4, 5, 6, 8, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(fixture.Path, new DateTime(2021, 4, 5, 6, 7, 10, DateTimeKind.Utc));
        File.SetAttributes(fixture.Path, FileAttributes.Archive | FileAttributes.Hidden);
        var request = await fixture.CreateRequestAsync();

        var result = await new WindowsRootBoundFileBasicMetadataEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, result.Status);
        Assert.IsNotNull(result.BasicMetadata);
        Assert.AreEqual(request.DestinationDirectoryIdentity, result.CurrentDestinationDirectory.Identity);
        Assert.AreEqual(request.DestinationIdentity, result.CurrentDestination.Identity);
        Assert.AreEqual(
            unchecked((ulong)File.GetCreationTimeUtc(fixture.Path).ToFileTimeUtc()),
            result.BasicMetadata.CreationTimeFileTime);
        Assert.AreEqual(
            unchecked((ulong)File.GetLastWriteTimeUtc(fixture.Path).ToFileTimeUtc()),
            result.BasicMetadata.LastWriteTimeFileTime);
        Assert.AreEqual(
            (uint)(FileAttributes.Archive | FileAttributes.Hidden),
            result.BasicMetadata.StableCopiedAttributes);
    }

    [TestMethod]
    public async Task ExistingWriterCanCoexistWithAttributesOnlyEvidenceRead()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        await using var writer = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);

        var result = await new WindowsRootBoundFileBasicMetadataEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, result.Status);
        Assert.IsNotNull(result.BasicMetadata);
    }

    [TestMethod]
    public async Task WrongLeafIdentityFailsClosed()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        request = request with
        {
            DestinationIdentity = new FileIdentity(
                request.DestinationIdentity.VolumeSerialNumber,
                request.DestinationIdentity.FileReferenceNumber + 1),
        };

        var result = await new WindowsRootBoundFileBasicMetadataEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DifferentObject, result.Status);
        Assert.IsNull(result.BasicMetadata);
    }

    [TestMethod]
    public async Task ReplacedRootWithSameFileMovedBackFailsClosed()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        var originalRoot = fixture.Root + ".original";
        Directory.Move(fixture.Root, originalRoot);
        Directory.CreateDirectory(fixture.Root);
        File.Move(System.IO.Path.Combine(originalRoot, "payload.bin"), fixture.Path);
        fixture.AddCleanupRoot(originalRoot);

        var result = await new WindowsRootBoundFileBasicMetadataEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DestinationRootChanged, result.Status);
        Assert.IsNull(result.BasicMetadata);
    }

    private sealed class ReaderFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileOp.BasicMetadata.Reader.Tests",
            Guid.NewGuid().ToString("N"));
        private string? _additionalCleanupRoot;

        public ReaderFixture()
        {
            Directory.CreateDirectory(_root);
            Root = _root;
            Path = System.IO.Path.Combine(Root, "payload.bin");
            File.WriteAllText(Path, "content");
        }

        public string Root { get; }
        public string Path { get; }
        public void AddCleanupRoot(string path) => _additionalCleanupRoot = path;

        public async Task<FileContentFingerprintReadRequest> CreateRequestAsync()
        {
            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var root = await resolver.ResolveAsync(Root);
            var leaf = await resolver.ResolveAsync(Path);
            Assert.AreEqual(FileOperationCanonicalPathState.Directory, root.State);
            Assert.AreEqual(FileOperationCanonicalPathState.File, leaf.State);
            Assert.IsNotNull(root.Identity);
            Assert.IsNotNull(leaf.Identity);
            return new FileContentFingerprintReadRequest(
                root.CanonicalPath,
                root.Identity.Value,
                leaf.CanonicalPath,
                leaf.Identity.Value);
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
