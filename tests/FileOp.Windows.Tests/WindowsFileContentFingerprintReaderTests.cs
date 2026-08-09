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
public sealed class WindowsFileContentFingerprintReaderTests
{
    [TestMethod]
    public async Task ReaderHashesPrimaryStreamAndPreservesIdentityEvidence()
    {
        using var fixture = new ReaderFixture();
        var payload = new byte[(1024 * 1024) + 211];
        new Random(20260809).NextBytes(payload);
        await File.WriteAllBytesAsync(fixture.Path, payload);
        var expected = await ResolveFileAsync(fixture.Path);

        var result = await new WindowsFileContentFingerprintReader()
            .ReadAsync(expected.CanonicalPath, expected.Identity!.Value);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, result.Status);
        Assert.AreEqual(expected.Identity, result.CurrentDestination.Identity);
        Assert.IsTrue(string.Equals(
            expected.CanonicalPath,
            result.CurrentDestination.CanonicalPath,
            StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(result.ContentFingerprint);
        Assert.AreEqual(FileContentFingerprintAlgorithm.Sha256, result.ContentFingerprint.Algorithm);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            result.ContentFingerprint.HexDigest);
    }

    [TestMethod]
    public async Task EmptyFileProducesStandardSha256Digest()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllBytesAsync(fixture.Path, Array.Empty<byte>());
        var expected = await ResolveFileAsync(fixture.Path);

        var result = await new WindowsFileContentFingerprintReader()
            .ReadAsync(expected.CanonicalPath, expected.Identity!.Value);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, result.Status);
        Assert.AreEqual(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            result.ContentFingerprint?.HexDigest);
    }

    [TestMethod]
    public async Task DifferentExpectedIdentityIsRejectedBeforeHashEvidence()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var expected = await ResolveFileAsync(fixture.Path);
        var wrong = new FileIdentity(
            expected.Identity!.Value.VolumeSerialNumber,
            expected.Identity.Value.FileReferenceNumber + 1);

        var result = await new WindowsFileContentFingerprintReader()
            .ReadAsync(expected.CanonicalPath, wrong);

        Assert.AreEqual(FileContentFingerprintReadStatus.DifferentObject, result.Status);
        Assert.IsNull(result.ContentFingerprint);
        Assert.AreEqual(expected.Identity, result.CurrentDestination.Identity);
    }

    [TestMethod]
    public async Task MissingDestinationFailsClosedWithoutFingerprint()
    {
        using var fixture = new ReaderFixture();
        var result = await new WindowsFileContentFingerprintReader()
            .ReadAsync(fixture.Path, new FileIdentity(1, 1));

        Assert.AreEqual(FileContentFingerprintReadStatus.Missing, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    [TestMethod]
    public async Task DirectoryDestinationFailsClosedWithoutFingerprint()
    {
        using var fixture = new ReaderFixture(createFile: false);
        var directory = await new WindowsFileOperationCanonicalPathResolver()
            .ResolveAsync(fixture.Directory);
        Assert.AreEqual(FileOperationCanonicalPathState.Directory, directory.State);

        var result = await new WindowsFileContentFingerprintReader()
            .ReadAsync(directory.CanonicalPath, directory.Identity!.Value);

        Assert.AreEqual(FileContentFingerprintReadStatus.UnexpectedType, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    [TestMethod]
    public async Task ExistingWriterCausesBusyInsteadOfWeakReadProof()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var expected = await ResolveFileAsync(fixture.Path);
        await using var writer = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite);

        var result = await new WindowsFileContentFingerprintReader()
            .ReadAsync(expected.CanonicalPath, expected.Identity!.Value);

        Assert.AreEqual(FileContentFingerprintReadStatus.Busy, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    [TestMethod]
    public async Task RestrictiveExistingReaderAlsoCausesBusy()
    {
        using var fixture = new ReaderFixture();
        await File.WriteAllTextAsync(fixture.Path, "content");
        var expected = await ResolveFileAsync(fixture.Path);
        await using var reader = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        var result = await new WindowsFileContentFingerprintReader()
            .ReadAsync(expected.CanonicalPath, expected.Identity!.Value);

        Assert.AreEqual(FileContentFingerprintReadStatus.Busy, result.Status);
        Assert.IsNull(result.ContentFingerprint);
    }

    private static async Task<FileOperationCanonicalPath> ResolveFileAsync(string path)
    {
        var resolved = await new WindowsFileOperationCanonicalPathResolver().ResolveAsync(path);
        Assert.AreEqual(FileOperationCanonicalPathState.File, resolved.State);
        Assert.IsFalse(resolved.IsLeafReparsePoint);
        Assert.IsNotNull(resolved.Identity);
        return resolved;
    }

    private sealed class ReaderFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileOp.RecoveryContent.Tests",
            Guid.NewGuid().ToString("N"));

        public ReaderFixture(bool createFile = true)
        {
            Directory.CreateDirectory(_root);
            Directory = _root;
            Path = System.IO.Path.Combine(_root, "payload.bin");
            if (createFile)
            {
                File.WriteAllBytes(Path, Array.Empty<byte>());
            }
        }

        public string Directory { get; }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(_root, recursive: true);
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
