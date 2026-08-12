using FileOp.Core.Storage;
using FileOp.Windows.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsSameSizeContentVerifierTests
{
    [TestMethod]
    public async Task FullyHashesBoundedSampleAndReturnsOnlyMatchingSets()
    {
        using var temp = new TempDirectory();
        var content = Enumerable.Range(0, 128 * 1024)
            .Select(static index => unchecked((byte)(index * 31)))
            .ToArray();
        var different = content.ToArray();
        different[^1] ^= 0x5A;
        var first = temp.Write("first.bin", content);
        var second = temp.Write("second.bin", content);
        var third = temp.Write("third.bin", different);
        var group = Group(content.Length, first, second, third);
        var policy = new StorageSameSizeContentVerificationPolicy(
            MaxFiles: 8,
            MaxTotalBytesRead: content.Length * 3L,
            BufferSizeBytes: 64 * 1024);
        var verifiedAt = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var verifier = new WindowsSameSizeContentVerifier(() => verifiedAt);

        var result = await verifier.VerifyAsync(group, policy);

        Assert.AreEqual(StorageSameSizeContentVerificationStatus.Completed, result.Status);
        Assert.AreEqual(verifiedAt, result.VerifiedAt);
        Assert.AreEqual(3, result.SelectedFileCount);
        Assert.AreEqual(3, result.FullyHashedFileCount);
        Assert.AreEqual(content.Length * 3L, result.BytesRead);
        Assert.AreEqual(1, result.MatchingSets.Count);
        CollectionAssert.AreEquivalent(
            new[] { first, second },
            result.MatchingSets[0].Paths.ToArray());
        Assert.IsTrue(result.HasVerifiedDuplicateEvidence);
        Assert.AreEqual((long)content.Length, result.VerifiedLogicalDuplicateBytes);
        Assert.IsNotNull(result.PhysicalReclaim);
        StringAssert.Contains(result.Detail, "Hash matches are verified logical duplicate evidence");
    }

    [TestMethod]
    public async Task BudgetLimitRefusesToReadEvenNonexistentCandidates()
    {
        var logicalBytes = 1024L * 1024;
        var group = Group(
            logicalBytes,
            @"C:\does-not-exist\first.bin",
            @"C:\does-not-exist\second.bin");
        var policy = new StorageSameSizeContentVerificationPolicy(
            MaxFiles: 8,
            MaxTotalBytesRead: logicalBytes * 2 - 1,
            BufferSizeBytes: 64 * 1024);
        var verifier = new WindowsSameSizeContentVerifier();

        var result = await verifier.VerifyAsync(group, policy);

        Assert.AreEqual(StorageSameSizeContentVerificationStatus.BudgetLimited, result.Status);
        Assert.AreEqual(0, result.SelectedFileCount);
        Assert.AreEqual(0, result.FullyHashedFileCount);
        Assert.AreEqual(0L, result.BytesRead);
        Assert.IsFalse(result.HasVerifiedDuplicateEvidence);
        Assert.AreEqual(0L, result.VerifiedLogicalDuplicateBytes);
        Assert.IsNull(result.PhysicalReclaim);
        StringAssert.Contains(result.Detail, "No content was read");
    }

    [TestMethod]
    public async Task ChangedLengthFailsClosedBeforeHashing()
    {
        using var temp = new TempDirectory();
        var first = temp.Write("first.bin", new byte[96 * 1024]);
        var second = temp.Write("second.bin", new byte[64 * 1024]);
        var group = Group(96 * 1024, first, second);
        var verifier = new WindowsSameSizeContentVerifier();

        var result = await verifier.VerifyAsync(
            group,
            new StorageSameSizeContentVerificationPolicy(
                MaxFiles: 2,
                MaxTotalBytesRead: 512 * 1024,
                BufferSizeBytes: 64 * 1024));

        Assert.AreEqual(StorageSameSizeContentVerificationStatus.CandidateChanged, result.Status);
        Assert.AreEqual(0, result.FullyHashedFileCount);
        Assert.AreEqual(0L, result.BytesRead);
        Assert.AreEqual(0, result.MatchingSets.Count);
        Assert.IsNull(result.PhysicalReclaim);
        StringAssert.Contains(result.Detail, "Refresh Optimize");
    }

    [TestMethod]
    public async Task IncompatibleWriterKeepsGroupUnverified()
    {
        using var temp = new TempDirectory();
        var content = new byte[64 * 1024];
        var first = temp.Write("first.bin", content);
        var second = temp.Write("second.bin", content);
        using var writer = new FileStream(
            second,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var verifier = new WindowsSameSizeContentVerifier();

        var result = await verifier.VerifyAsync(
            Group(content.Length, first, second),
            new StorageSameSizeContentVerificationPolicy(
                MaxFiles: 2,
                MaxTotalBytesRead: content.Length * 2L,
                BufferSizeBytes: 64 * 1024));

        Assert.AreEqual(StorageSameSizeContentVerificationStatus.Unavailable, result.Status);
        Assert.AreEqual(0, result.MatchingSets.Count);
        Assert.IsFalse(result.HasVerifiedDuplicateEvidence);
        Assert.IsNull(result.PhysicalReclaim);
        StringAssert.Contains(result.Detail, "Partial hash evidence was discarded");
    }

    [TestMethod]
    public async Task ByteBudgetSelectsOnlyWholeFilesAndNeverOverreads()
    {
        using var temp = new TempDirectory();
        var content = new byte[80 * 1024];
        var paths = Enumerable.Range(0, 5)
            .Select(index => temp.Write($"{index}.bin", content))
            .ToArray();
        var budget = content.Length * 3L;
        var verifier = new WindowsSameSizeContentVerifier();

        var result = await verifier.VerifyAsync(
            Group(content.Length, paths),
            new StorageSameSizeContentVerificationPolicy(
                MaxFiles: 8,
                MaxTotalBytesRead: budget,
                BufferSizeBytes: 64 * 1024));

        Assert.AreEqual(StorageSameSizeContentVerificationStatus.Completed, result.Status);
        Assert.AreEqual(3, result.SelectedFileCount);
        Assert.AreEqual(3, result.FullyHashedFileCount);
        Assert.AreEqual(budget, result.BytesRead);
        Assert.AreEqual(1, result.MatchingSets.Count);
        Assert.AreEqual(3, result.MatchingSets[0].FileCount);
        Assert.AreEqual(content.Length * 2L, result.VerifiedLogicalDuplicateBytes);
        Assert.IsNotNull(result.PhysicalReclaim);
    }

    [TestMethod]
    public async Task PreCancelledVerificationDoesNotOpenFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var verifier = new WindowsSameSizeContentVerifier();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await verifier.VerifyAsync(
                Group(
                    1024,
                    @"C:\does-not-exist\first.bin",
                    @"C:\does-not-exist\second.bin"),
                new StorageSameSizeContentVerificationPolicy(
                    MaxFiles: 2,
                    MaxTotalBytesRead: 4096,
                    BufferSizeBytes: 64 * 1024),
                cancellation.Token));
    }

    private static StorageSameSizeCandidateGroup Group(long logicalBytes, params string[] paths) =>
        new(
            logicalBytes,
            paths.Length,
            logicalBytes * Math.Max(0, paths.Length - 1),
            paths.Select(path => new StorageSameSizeCandidateFile(
                path,
                Path.GetFileName(path),
                Path.GetExtension(path),
                StorageFileCategoryClassifier.Classify(Path.GetExtension(path)),
                logicalBytes,
                AllocatedBytes: null,
                DateTimeOffset.UnixEpoch)).ToArray());

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "fileop-content-verification-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string Write(string name, byte[] content)
        {
            var path = Path.Combine(_path, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
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
