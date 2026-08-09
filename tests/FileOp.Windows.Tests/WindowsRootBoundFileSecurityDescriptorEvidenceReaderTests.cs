using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsRootBoundFileSecurityDescriptorEvidenceReaderTests
{
    [TestMethod]
    public async Task ReaderObservesStableOwnerGroupDaclDigest()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        var reader = new WindowsRootBoundFileSecurityDescriptorEvidenceReader();

        var first = await reader.ReadAsync(request);
        var second = await reader.ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, first.Status);
        Assert.IsNotNull(first.SecurityDescriptor);
        Assert.AreEqual(0x7u, first.SecurityDescriptor.SecurityInformation);
        Assert.AreEqual(64, first.SecurityDescriptor.Sha256HexDigest.Length);
        Assert.AreEqual(first.SecurityDescriptor, second.SecurityDescriptor);
        Assert.AreEqual(request.DestinationDirectoryIdentity, first.CurrentDestinationDirectory.Identity);
        Assert.AreEqual(request.DestinationIdentity, first.CurrentDestination.Identity);
    }

    [TestMethod]
    public async Task ExistingWriterCanCoexistWithReadControlEvidenceRead()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        await using var writer = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);

        var result = await new WindowsRootBoundFileSecurityDescriptorEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, result.Status);
        Assert.IsNotNull(result.SecurityDescriptor);
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

        var result = await new WindowsRootBoundFileSecurityDescriptorEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DifferentObject, result.Status);
        Assert.IsNull(result.SecurityDescriptor);
    }

    [TestMethod]
    public async Task MissingLeafFailsClosed()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        File.Delete(fixture.Path);

        var result = await new WindowsRootBoundFileSecurityDescriptorEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Missing, result.Status);
        Assert.IsNull(result.SecurityDescriptor);
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

        var result = await new WindowsRootBoundFileSecurityDescriptorEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DestinationRootChanged, result.Status);
        Assert.IsNull(result.SecurityDescriptor);
    }

    private sealed class ReaderFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileOp.SecurityDescriptor.Reader.Tests",
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
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
