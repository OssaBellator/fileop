using FileOp.Windows.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsCurrentFilePhysicalEvidenceReaderTests
{
    [TestMethod]
    public void TwoOpenHandlesReportSameCurrentIdentityAndAllocation()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Current physical file evidence uses Windows handle metadata APIs.");
        }

        using var temp = new TempDirectory();
        var path = temp.Write("sample.bin", new byte[192 * 1024]);
        using var first = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var second = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new WindowsCurrentFilePhysicalEvidenceReader();

        var firstEvidence = reader.Read(first, path);
        var secondEvidence = reader.Read(second, path);

        Assert.AreEqual(Path.GetFullPath(path), firstEvidence.Path);
        Assert.AreEqual(firstEvidence.Identity, secondEvidence.Identity);
        Assert.AreEqual(firstEvidence.HardLinkCount, secondEvidence.HardLinkCount);
        Assert.AreEqual(firstEvidence.AllocatedBytes, secondEvidence.AllocatedBytes);
        Assert.IsTrue(firstEvidence.HardLinkCount >= 1);
        Assert.IsTrue(firstEvidence.AllocatedBytes >= 0);
    }

    [TestMethod]
    public void DisposedHandleIsRejectedBeforeNativeRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Current physical file evidence uses Windows handle metadata APIs.");
        }

        using var temp = new TempDirectory();
        var path = temp.Write("sample.bin", new byte[64 * 1024]);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Dispose();
        var reader = new WindowsCurrentFilePhysicalEvidenceReader();

        Assert.ThrowsException<ObjectDisposedException>(() => reader.Read(stream, path));
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "fileop-physical-evidence-" + Guid.NewGuid().ToString("N"));

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
