using FileOp.Core.Models;
using FileOp.Core.Search;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class InMemoryStorageAnalyticsTests
{
    [TestMethod]
    public async Task AnalyzeDirectoryMatchesRecursiveStorageSemantics()
    {
        using var index = new InMemoryFileIndex();
        await index.AddBatchAsync(
        [
            Directory(@"C:\Data\Alpha", @"C:\Data", "Alpha"),
            File(@"C:\Data\Alpha\a.bin", @"C:\Data\Alpha", "a.bin", 100, 128),
            Directory(@"C:\Data\Alpha\Nested", @"C:\Data\Alpha", "Nested"),
            File(@"C:\Data\Alpha\Nested\b.bin", @"C:\Data\Alpha\Nested", "b.bin", 200, 256),
            File(@"C:\Data\root.txt", @"C:\Data", "root.txt", 50, 64),
        ]);

        var result = await index.AnalyzeDirectoryAsync(@"C:\Data", maxEntries: 10);

        Assert.AreEqual(350L, result.LogicalBytes);
        Assert.AreEqual(448L, result.AllocatedBytes);
        Assert.AreEqual(3, result.FileCount);
        Assert.AreEqual(2, result.DirectoryCount);
        Assert.AreEqual(2, result.DirectEntryCount);
        Assert.AreEqual(@"C:\Data\Alpha", result.Entries[0].Path);
        Assert.AreEqual(300L, result.Entries[0].LogicalBytes);
        Assert.AreEqual(384L, result.Entries[0].AllocatedBytes);
        Assert.AreEqual(2, result.Entries[0].FileCount);
        Assert.AreEqual(2, result.Entries[0].DirectoryCount);
    }

    [TestMethod]
    public async Task AnalyzeDirectoryPreservesUnknownAllocatedSize()
    {
        using var index = new InMemoryFileIndex();
        await index.AddBatchAsync(
        [
            File(@"C:\Data\known.bin", @"C:\Data", "known.bin", 100, 128),
            File(@"C:\Data\unknown.bin", @"C:\Data", "unknown.bin", 200, null),
        ]);

        var result = await index.AnalyzeDirectoryAsync(@"C:\Data", maxEntries: 10);

        Assert.IsNull(result.AllocatedBytes);
        Assert.AreEqual(128L, result.Entries.Single(entry => entry.Name == "known.bin").AllocatedBytes);
        Assert.IsNull(result.Entries.Single(entry => entry.Name == "unknown.bin").AllocatedBytes);
    }

    private static FileRecord Directory(string path, string parentPath, string name) =>
        new(
            path,
            name,
            parentPath,
            string.Empty,
            0,
            true,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Directory);

    private static FileRecord File(
        string path,
        string parentPath,
        string name,
        long logicalBytes,
        long? allocatedBytes) =>
        new(
            path,
            name,
            parentPath,
            Path.GetExtension(name),
            logicalBytes,
            false,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Normal,
            AllocatedLength: allocatedBytes);
}
