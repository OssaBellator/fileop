using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsRootBoundFileContentFingerprintReaderTests
{
    [TestMethod]
    public async Task ReaderHashesLeafRelativeToVerifiedRoot()
    {
        using var fixture = new ReaderFixture();
        var payload = new byte[(1024 * 1024) + 211];
        new Random(20260815).NextBytes(payload);
        await File.WriteAllBytesAsync(fixture.Path, payload);
        var request = await fixture.CreateRequestAsync();

        var result = await new WindowsRootBoundFileContentFingerprintReader()
            .ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, result.Status);
        Assert.IsNotNull(result.CurrentDestinationDirectory);
        Assert.AreEqual(request.DestinationDirectoryIdentity, result.CurrentDestinationDirectory.Identity);
        Assert.AreEqual(request.DestinationIdentity, result.CurrentDestination.Identity);
        Assert.IsNotNull(result.ContentFingerprint);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            result.ContentFingerprint!.HexDigest);
    }

    [TestMethod]
    public async Task WrongRootIdentityIsRejectedBeforeLeafEvidence()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var request = await fixture.CreateRequestAsync();
        request = request with
        {
            DestinationDirectoryIdentity = new FileIdentity(
                request.DestinationDirectoryIdentity.VolumeSerialNumber,
                request.DestinationDirectoryIdentity.FileReferenceNumber + 1),
        };

        var result = await new WindowsRootBoundFileContentFingerprintReader()
            .ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DestinationRootChanged, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    [TestMethod]
    public async Task WrongLeafIdentityIsRejectedBeforeHashEvidence()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var request = await fixture.CreateRequestAsync();
        request = request with
        {
            DestinationIdentity = new FileIdentity(
                request.DestinationIdentity.VolumeSerialNumber,
                request.DestinationIdentity.FileReferenceNumber + 1),
        };

        var result = await new WindowsRootBoundFileContentFingerprintReader()
            .ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DifferentObject, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    [TestMethod]
    public async Task MissingLeafFailsClosedWithoutFingerprint()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var request = await fixture.CreateRequestAsync();
        File.Delete(fixture.Path);

        var result = await new WindowsRootBoundFileContentFingerprintReader()
            .ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Missing, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    [TestMethod]
    public async Task ExistingWriterCausesBusyInsteadOfWeakRootBoundProof()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var request = await fixture.CreateRequestAsync();
        await using var writer = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite);

        var result = await new WindowsRootBoundFileContentFingerprintReader()
            .ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Busy, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    [TestMethod]
    public async Task RestrictiveExistingReaderAlsoCausesBusyForRootBoundReader()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var request = await fixture.CreateRequestAsync();
        await using var reader = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = await new WindowsRootBoundFileContentFingerprintReader()
            .ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Busy, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    [TestMethod]
    public async Task ReplacedRootWithSameFileMovedBackIsRejected()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var request = await fixture.CreateRequestAsync();
        var originalRoot = fixture.Root + ".original";

        Directory.Move(fixture.Root, originalRoot);
        Directory.CreateDirectory(fixture.Root);
        File.Move(
            System.IO.Path.Combine(originalRoot, "payload.bin"),
            fixture.Path);
        fixture.AddCleanupRoot(originalRoot);

        var result = await new WindowsRootBoundFileContentFingerprintReader()
            .ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DestinationRootChanged, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    private sealed class ReaderFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileOp.RootBoundRecovery.Tests",
            Guid.NewGuid().ToString("N"));
        private string? _additionalCleanupRoot;

        public ReaderFixture()
        {
            Directory.CreateDirectory(_root);
            Root = _root;
            Path = System.IO.Path.Combine(Root, "payload.bin");
            File.WriteAllBytes(Path, Array.Empty<byte>());
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
