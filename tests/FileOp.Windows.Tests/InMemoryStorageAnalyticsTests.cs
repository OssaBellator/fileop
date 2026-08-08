using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Core.Storage;

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
        Assert.AreEqual(3, result.UniqueFileCount);
        Assert.AreEqual(0, result.HardLinkAliasCount);
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

    [TestMethod]
    public async Task AnalyzeDirectoryCountsHardLinkedNamesOnceForPhysicalUsage()
    {
        var identity = new FileIdentity(0xAABB, 0x1122);
        using var index = new InMemoryFileIndex();
        await index.AddBatchAsync(
        [
            Directory(@"C:\Data\Alpha", @"C:\Data", "Alpha"),
            Directory(@"C:\Data\Beta", @"C:\Data", "Beta"),
            File(@"C:\Data\Alpha\shared.bin", @"C:\Data\Alpha", "shared.bin", 100, 128, identity),
            File(@"C:\Data\Beta\shared.bin", @"C:\Data\Beta", "shared.bin", 100, 128, identity),
        ]);

        var result = await index.AnalyzeDirectoryAsync(@"C:\Data", maxEntries: 10);

        Assert.AreEqual(200L, result.LogicalBytes);
        Assert.AreEqual(128L, result.AllocatedBytes);
        Assert.AreEqual(2, result.FileCount);
        Assert.AreEqual(1, result.UniqueFileCount);
        Assert.AreEqual(1, result.HardLinkAliasCount);
        Assert.AreEqual(128L, result.Entries.Single(entry => entry.Name == "Alpha").AllocatedBytes);
        Assert.AreEqual(0L, result.Entries.Single(entry => entry.Name == "Beta").AllocatedBytes);
        Assert.AreEqual(1, result.Entries.Single(entry => entry.Name == "Beta").HardLinkAliasCount);
    }

    [TestMethod]
    public async Task AnalyzeFileTypesGroupsNestedFilesAndNormalizesExtensions()
    {
        using var index = new InMemoryFileIndex();
        await index.AddBatchAsync(
        [
            Directory(@"C:\Data\Images", @"C:\Data", "Images"),
            File(@"C:\Data\Images\one.JPG", @"C:\Data\Images", "one.JPG", 100, 128),
            File(@"C:\Data\Images\two.jpg", @"C:\Data\Images", "two.jpg", 200, 256),
            File(@"C:\Data\notes.txt", @"C:\Data", "notes.txt", 50, 64),
            File(@"C:\Data\README", @"C:\Data", "README", 25, 32),
        ]);

        var result = await index.AnalyzeFileTypesAsync(@"C:\Data", maxTypes: 10);

        Assert.AreEqual(375L, result.LogicalBytes);
        Assert.AreEqual(480L, result.AllocatedBytes);
        Assert.AreEqual(4, result.FileCount);
        Assert.AreEqual(4, result.UniqueFileCount);
        Assert.AreEqual(0, result.HardLinkAliasCount);
        Assert.AreEqual(3, result.TypeCount);

        var jpg = result.Types.Single(entry => entry.Extension == "jpg");
        Assert.AreEqual(StorageFileCategory.Images, jpg.Category);
        Assert.AreEqual(300L, jpg.LogicalBytes);
        Assert.AreEqual(384L, jpg.AllocatedBytes);
        Assert.AreEqual(2, jpg.FileCount);

        var noExtension = result.Types.Single(entry => entry.Extension == string.Empty);
        Assert.AreEqual(StorageFileCategory.NoExtension, noExtension.Category);
        Assert.AreEqual(25L, noExtension.LogicalBytes);
    }

    [TestMethod]
    public async Task AnalyzeFileTypesCountsCrossExtensionHardLinksOnceForPhysicalUsage()
    {
        var identity = new FileIdentity(0xAABB, 0x2233);
        using var index = new InMemoryFileIndex();
        await index.AddBatchAsync(
        [
            Directory(@"C:\Data\A", @"C:\Data", "A"),
            Directory(@"C:\Data\B", @"C:\Data", "B"),
            File(@"C:\Data\A\shared.jpg", @"C:\Data\A", "shared.jpg", 100, 128, identity),
            File(@"C:\Data\B\shared.png", @"C:\Data\B", "shared.png", 100, 128, identity),
        ]);

        var result = await index.AnalyzeFileTypesAsync(@"C:\Data", maxTypes: 10);

        Assert.AreEqual(200L, result.LogicalBytes);
        Assert.AreEqual(128L, result.AllocatedBytes);
        Assert.AreEqual(2, result.FileCount);
        Assert.AreEqual(1, result.UniqueFileCount);
        Assert.AreEqual(1, result.HardLinkAliasCount);

        var jpg = result.Types.Single(entry => entry.Extension == "jpg");
        var png = result.Types.Single(entry => entry.Extension == "png");
        Assert.AreEqual(128L, jpg.AllocatedBytes);
        Assert.AreEqual(0, jpg.HardLinkAliasCount);
        Assert.AreEqual(0L, png.AllocatedBytes);
        Assert.AreEqual(1, png.HardLinkAliasCount);
        Assert.AreEqual(0, png.UniqueFileCount);
    }

    [TestMethod]
    public async Task AnalyzeFileTypesLimitPreservesTotalsAndUnknownAllocation()
    {
        using var index = new InMemoryFileIndex();
        await index.AddBatchAsync(
        [
            File(@"C:\Data\a.bin", @"C:\Data", "a.bin", 300, 384),
            File(@"C:\Data\b.txt", @"C:\Data", "b.txt", 200, null),
            File(@"C:\Data\c.jpg", @"C:\Data", "c.jpg", 100, 128),
        ]);

        var result = await index.AnalyzeFileTypesAsync(@"C:\Data", maxTypes: 1);

        Assert.AreEqual(600L, result.LogicalBytes);
        Assert.IsNull(result.AllocatedBytes);
        Assert.AreEqual(3, result.FileCount);
        Assert.AreEqual(3, result.TypeCount);
        Assert.AreEqual(1, result.Types.Count);
        Assert.AreEqual("bin", result.Types[0].Extension);
        Assert.AreEqual(300L, result.Types[0].LogicalBytes);
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
        long? allocatedBytes,
        FileIdentity? identity = null) =>
        new(
            path,
            name,
            parentPath,
            Path.GetExtension(name),
            logicalBytes,
            false,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Normal,
            Identity: identity,
            AllocatedLength: allocatedBytes);
}
