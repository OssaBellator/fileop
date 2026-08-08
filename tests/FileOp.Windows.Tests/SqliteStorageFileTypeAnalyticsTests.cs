using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class SqliteStorageFileTypeAnalyticsTests
{
    [TestMethod]
    public async Task AnalyzeFileTypesGroupsNestedFilesAndNormalizesExtensions()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync(
            [
                Directory(@"C:\Data\Images", @"C:\Data", "Images"),
                File(@"C:\Data\Images\one.JPG", @"C:\Data\Images", "one.JPG", 100, 128),
                File(@"C:\Data\Images\two.jpg", @"C:\Data\Images", "two.jpg", 200, 256),
                File(@"C:\Data\notes.txt", @"C:\Data", "notes.txt", 50, 64),
                File(@"C:\Data\README", @"C:\Data", "README", 25, 32),
            ]);
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeFileTypesAsync(@"C:\Data", maxTypes: 10);

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
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task AnalyzeFileTypesCountsCrossExtensionHardLinksOnceForPhysicalUsage()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var identity = new FileIdentity(0xAABB, 0x2233);
            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync(
            [
                Directory(@"C:\Data\A", @"C:\Data", "A"),
                Directory(@"C:\Data\B", @"C:\Data", "B"),
                File(@"C:\Data\A\shared.jpg", @"C:\Data\A", "shared.jpg", 100, 128, identity),
                File(@"C:\Data\B\shared.png", @"C:\Data\B", "shared.png", 100, 128, identity),
            ]);
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeFileTypesAsync(@"C:\Data", maxTypes: 10);

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
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task AnalyzeFileTypesLimitPreservesTotalsAndUnknownAllocation()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync(
            [
                File(@"C:\Data\a.bin", @"C:\Data", "a.bin", 300, 384),
                File(@"C:\Data\b.txt", @"C:\Data", "b.txt", 200, null),
                File(@"C:\Data\c.jpg", @"C:\Data", "c.jpg", 100, 128),
            ]);
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeFileTypesAsync(@"C:\Data", maxTypes: 1);

            Assert.AreEqual(600L, result.LogicalBytes);
            Assert.IsNull(result.AllocatedBytes);
            Assert.AreEqual(3, result.FileCount);
            Assert.AreEqual(3, result.TypeCount);
            Assert.AreEqual(1, result.Types.Count);
            Assert.AreEqual("bin", result.Types[0].Extension);
            Assert.AreEqual(300L, result.Types[0].LogicalBytes);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task AnalyzeFileTypesReturnsEmptySummaryForUnknownOrEmptyDirectory()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            using var analytics = new SqliteStorageAnalytics(databasePath);

            var result = await analytics.AnalyzeFileTypesAsync(@"C:\Empty", maxTypes: 10);

            Assert.AreEqual(0L, result.LogicalBytes);
            Assert.AreEqual(0L, result.AllocatedBytes);
            Assert.AreEqual(0, result.FileCount);
            Assert.AreEqual(0, result.UniqueFileCount);
            Assert.AreEqual(0, result.HardLinkAliasCount);
            Assert.AreEqual(0, result.TypeCount);
            Assert.AreEqual(0, result.Types.Count);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
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

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"fileop-storage-types-{Guid.NewGuid():N}.sqlite");

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
