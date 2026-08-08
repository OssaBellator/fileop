using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class SqliteStorageAnalyticsTests
{
    [TestMethod]
    public async Task AnalyzeDirectoryAggregatesDirectChildrenRecursively()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync(CreateFixture());
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeDirectoryAsync(@"C:\Data", maxEntries: 10);

            Assert.AreEqual(@"C:\Data", result.RootPath);
            Assert.AreEqual(750L, result.LogicalBytes);
            Assert.AreEqual(960L, result.AllocatedBytes);
            Assert.AreEqual(4, result.FileCount);
            Assert.AreEqual(4, result.UniqueFileCount);
            Assert.AreEqual(0, result.HardLinkAliasCount);
            Assert.AreEqual(3, result.DirectoryCount);
            Assert.AreEqual(3, result.DirectEntryCount);
            Assert.AreEqual(3, result.Entries.Count);

            var beta = result.Entries[0];
            Assert.AreEqual(@"C:\Data\Beta", beta.Path);
            Assert.IsTrue(beta.IsDirectory);
            Assert.AreEqual(400L, beta.LogicalBytes);
            Assert.AreEqual(512L, beta.AllocatedBytes);
            Assert.AreEqual(1, beta.FileCount);
            Assert.AreEqual(1, beta.UniqueFileCount);
            Assert.AreEqual(0, beta.HardLinkAliasCount);
            Assert.AreEqual(1, beta.DirectoryCount);

            var alpha = result.Entries[1];
            Assert.AreEqual(@"C:\Data\Alpha", alpha.Path);
            Assert.AreEqual(300L, alpha.LogicalBytes);
            Assert.AreEqual(384L, alpha.AllocatedBytes);
            Assert.AreEqual(2, alpha.FileCount);
            Assert.AreEqual(2, alpha.UniqueFileCount);
            Assert.AreEqual(2, alpha.DirectoryCount);

            var rootFile = result.Entries[2];
            Assert.AreEqual(@"C:\Data\root.txt", rootFile.Path);
            Assert.IsFalse(rootFile.IsDirectory);
            Assert.AreEqual(50L, rootFile.LogicalBytes);
            Assert.AreEqual(64L, rootFile.AllocatedBytes);
            Assert.AreEqual(1, rootFile.FileCount);
            Assert.AreEqual(0, rootFile.DirectoryCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task AnalyzeDirectoryLimitDoesNotChangeWholeRootTotals()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync(CreateFixture());
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeDirectoryAsync(@"C:\Data", maxEntries: 1);

            Assert.AreEqual(1, result.Entries.Count);
            Assert.AreEqual(3, result.DirectEntryCount);
            Assert.AreEqual(750L, result.LogicalBytes);
            Assert.AreEqual(960L, result.AllocatedBytes);
            Assert.AreEqual(4, result.FileCount);
            Assert.AreEqual(3, result.DirectoryCount);
            Assert.AreEqual(@"C:\Data\Beta", result.Entries[0].Path);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task AnalyzeDirectoryKeepsAllocatedSizeUnknownWhenAnyPhysicalFileIsUnknown()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync(
            [
                Directory(@"C:\Data\Known", @"C:\Data", "Known"),
                File(@"C:\Data\Known\known.bin", @"C:\Data\Known", "known.bin", 100, 128),
                Directory(@"C:\Data\Unknown", @"C:\Data", "Unknown"),
                File(@"C:\Data\Unknown\unknown.bin", @"C:\Data\Unknown", "unknown.bin", 200, null),
            ]);
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeDirectoryAsync(@"C:\Data", maxEntries: 10);

            Assert.IsNull(result.AllocatedBytes);
            Assert.AreEqual(300L, result.LogicalBytes);
            Assert.AreEqual(128L, result.Entries.Single(entry => entry.Name == "Known").AllocatedBytes);
            Assert.IsNull(result.Entries.Single(entry => entry.Name == "Unknown").AllocatedBytes);
            Assert.AreEqual(200L, result.Entries.Single(entry => entry.Name == "Unknown").TreemapBytes);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task AnalyzeDirectoryCountsHardLinkedNamesOnceForPhysicalUsage()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var identity = new FileIdentity(0xAABB, 0x1122);
            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync(
            [
                Directory(@"C:\Data\Alpha", @"C:\Data", "Alpha"),
                Directory(@"C:\Data\Beta", @"C:\Data", "Beta"),
                File(@"C:\Data\Alpha\shared.bin", @"C:\Data\Alpha", "shared.bin", 100, 128, identity),
                File(@"C:\Data\Beta\shared.bin", @"C:\Data\Beta", "shared.bin", 100, 128, identity),
            ]);
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeDirectoryAsync(@"C:\Data", maxEntries: 10);

            Assert.AreEqual(200L, result.LogicalBytes);
            Assert.AreEqual(128L, result.AllocatedBytes);
            Assert.AreEqual(2, result.FileCount);
            Assert.AreEqual(1, result.UniqueFileCount);
            Assert.AreEqual(1, result.HardLinkAliasCount);

            var alpha = result.Entries.Single(entry => entry.Name == "Alpha");
            var beta = result.Entries.Single(entry => entry.Name == "Beta");
            Assert.AreEqual(128L, alpha.AllocatedBytes);
            Assert.AreEqual(0, alpha.HardLinkAliasCount);
            Assert.AreEqual(0L, beta.AllocatedBytes);
            Assert.AreEqual(1, beta.HardLinkAliasCount);
            Assert.AreEqual(0, beta.UniqueFileCount);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task AnalyzeDirectoryReturnsEmptySummaryForUnknownOrEmptyDirectory()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeDirectoryAsync(@"C:\Empty", maxEntries: 10);

            Assert.AreEqual(0L, result.LogicalBytes);
            Assert.AreEqual(0L, result.AllocatedBytes);
            Assert.AreEqual(0, result.FileCount);
            Assert.AreEqual(0, result.UniqueFileCount);
            Assert.AreEqual(0, result.HardLinkAliasCount);
            Assert.AreEqual(0, result.DirectoryCount);
            Assert.AreEqual(0, result.DirectEntryCount);
            Assert.AreEqual(0, result.Entries.Count);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static IReadOnlyList<FileRecord> CreateFixture() =>
    [
        Directory(@"C:\Data\Alpha", @"C:\Data", "Alpha"),
        File(@"C:\Data\Alpha\a.bin", @"C:\Data\Alpha", "a.bin", 100, 128),
        Directory(@"C:\Data\Alpha\Nested", @"C:\Data\Alpha", "Nested"),
        File(@"C:\Data\Alpha\Nested\b.bin", @"C:\Data\Alpha\Nested", "b.bin", 200, 256),
        File(@"C:\Data\root.txt", @"C:\Data", "root.txt", 50, 64),
        Directory(@"C:\Data\Beta", @"C:\Data", "Beta"),
        File(@"C:\Data\Beta\c.bin", @"C:\Data\Beta", "c.bin", 400, 512),
    ];

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

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"fileop-storage-tests-{Guid.NewGuid():N}.sqlite");

    private static void DeleteDatabase(string databasePath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(databasePath + suffix);
            }
            catch (IOException)
            {
            }
        }
    }
}
