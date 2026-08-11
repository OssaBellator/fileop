using FileOp.Windows.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsCurrentReviewFileEvidenceReaderTests
{
    [TestMethod]
    public async Task ReadAsyncCapturesCurrentHandleIdentitySizeAndLastWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows handle metadata is available only on Windows.");
        }

        var root = Path.Combine(Path.GetTempPath(), "fileop-cleanup-readiness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "candidate.bin");
        try
        {
            await File.WriteAllBytesAsync(path, Enumerable.Range(0, 4096).Select(static value => (byte)value).ToArray());
            var expectedLastWrite = new DateTime(2026, 8, 1, 12, 34, 56, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, expectedLastWrite);
            expectedLastWrite = File.GetLastWriteTimeUtc(path);

            var reader = new WindowsCurrentReviewFileEvidenceReader();
            var evidence = await reader.ReadAsync(path);

            Assert.AreEqual(Path.GetFullPath(path), Path.GetFullPath(evidence.RequestedPath));
            Assert.AreEqual(Path.GetFullPath(path), Path.GetFullPath(evidence.CanonicalPath));
            Assert.AreEqual(new FileInfo(path).Length, evidence.LogicalBytes);
            Assert.IsTrue(evidence.AllocatedBytes >= 0);
            Assert.IsTrue(evidence.HardLinkCount > 0);
            Assert.AreEqual(expectedLastWrite.Ticks, evidence.LastWriteTimeUtc.UtcDateTime.Ticks);
            Assert.AreNotEqual(0UL, evidence.Identity.FileReferenceNumber);
            Assert.IsFalse(evidence.IsLeafReparsePoint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadAsyncRejectsDirectoryCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows handle metadata is available only on Windows.");
        }

        var root = Path.Combine(Path.GetTempPath(), "fileop-cleanup-readiness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var reader = new WindowsCurrentReviewFileEvidenceReader();
            await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
                await reader.ReadAsync(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
