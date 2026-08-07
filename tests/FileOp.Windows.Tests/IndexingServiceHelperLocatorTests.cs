using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingServiceHelperLocatorTests
{
    [TestMethod]
    public void ResolveAdjacentHelperReturnsExactInstalledHost()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var expected = Path.Combine(directory, IndexingServiceHelperLocator.HelperFileName);
            File.WriteAllBytes(expected, [0x4D, 0x5A]);

            var actual = IndexingServiceHelperLocator.ResolveAdjacentHelper(directory);

            Assert.AreEqual(Path.GetFullPath(expected), actual);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ResolveAdjacentHelperRejectsMissingHost()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            Assert.ThrowsExactly<FileNotFoundException>(() =>
                IndexingServiceHelperLocator.ResolveAdjacentHelper(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fileop-helper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
