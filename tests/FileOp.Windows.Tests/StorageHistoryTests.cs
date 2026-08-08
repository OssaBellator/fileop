using FileOp.Core.Search;
using FileOp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageHistoryTests
{
    [TestMethod]
    public async Task SaveAndLoadSnapshotsPreservesExactCategoriesAndOrdering()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var history = new SqliteStorageHistoryStore(databasePath);
            var first = Analysis(
                @"C:\Data",
                logical: 600,
                allocated: 640,
                files: 3,
                aliases: 0,
                typeCount: 3,
                [
                    Category(StorageFileCategory.Images, 400, 448, 2, 0, 2),
                    Category(StorageFileCategory.Documents, 200, 192, 1, 0, 1),
                ]);
            var second = Analysis(
                @"C:\Data",
                logical: 900,
                allocated: 1024,
                files: 5,
                aliases: 1,
                typeCount: 4,
                [
                    Category(StorageFileCategory.Images, 500, 512, 3, 1, 2),
                    Category(StorageFileCategory.Documents, 250, 256, 1, 0, 1),
                    Category(StorageFileCategory.Archives, 150, 256, 1, 0, 1),
                ]);

            await history.SaveSnapshotAsync(first, new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero));
            await history.SaveSnapshotAsync(second, new DateTimeOffset(2026, 8, 8, 2, 0, 0, TimeSpan.Zero));

            var snapshots = await history.GetSnapshotsAsync(@"c:\data\", limit: 10);

            Assert.AreEqual(2, snapshots.Count);
            Assert.AreEqual(900L, snapshots[0].LogicalBytes);
            Assert.AreEqual(1024L, snapshots[0].AllocatedBytes);
            Assert.AreEqual(4, snapshots[0].UniqueFileCount);
            Assert.AreEqual(3, snapshots[0].Categories.Count);
            Assert.AreEqual(StorageFileCategory.Archives, snapshots[0].Categories[0].Category);
            Assert.AreEqual(600L, snapshots[1].LogicalBytes);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task SameTimestampOverwriteIsIdempotentAndPruneCascadesCategories()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var history = new SqliteStorageHistoryStore(databasePath);
            var capturedAt = new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero);
            var initial = Analysis(
                @"C:\",
                100,
                128,
                1,
                0,
                1,
                [Category(StorageFileCategory.Data, 100, 128, 1, 0, 1)]);
            var replacement = Analysis(
                @"C:\",
                200,
                256,
                2,
                0,
                2,
                [
                    Category(StorageFileCategory.Data, 150, 192, 1, 0, 1),
                    Category(StorageFileCategory.Documents, 50, 64, 1, 0, 1),
                ]);

            var firstId = await history.SaveSnapshotAsync(initial, capturedAt);
            var replacementId = await history.SaveSnapshotAsync(replacement, capturedAt);
            Assert.AreEqual(firstId, replacementId);

            await history.SaveSnapshotAsync(
                Analysis(@"C:\", 300, 384, 3, 0, 3,
                [
                    Category(StorageFileCategory.Data, 150, 192, 1, 0, 1),
                    Category(StorageFileCategory.Documents, 50, 64, 1, 0, 1),
                    Category(StorageFileCategory.Images, 100, 128, 1, 0, 1),
                ]),
                capturedAt.AddHours(1));

            var beforePrune = await history.GetSnapshotsAsync(@"C:\", limit: 10);
            Assert.AreEqual(2, beforePrune.Count);
            Assert.AreEqual(200L, beforePrune[1].LogicalBytes);
            Assert.AreEqual(2, beforePrune[1].Categories.Count);

            var pruned = await history.PruneBeforeAsync(capturedAt.AddMinutes(30));
            Assert.AreEqual(1, pruned);
            var afterPrune = await history.GetSnapshotsAsync(@"C:\", limit: 10);
            Assert.AreEqual(1, afterPrune.Count);

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM storage_history_categories;";
            Assert.AreEqual(3L, (long)(command.ExecuteScalar() ?? -1L));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task NamespaceClearLeavesHistoricalSnapshotsIntact()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var index = new SqliteFileIndex(databasePath);
            using var history = new SqliteStorageHistoryStore(databasePath);
            await history.SaveSnapshotAsync(
                Analysis(@"C:\", 100, 128, 1, 0, 1,
                    [Category(StorageFileCategory.Data, 100, 128, 1, 0, 1)]),
                new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero));

            await index.ClearAsync();

            var snapshots = await history.GetSnapshotsAsync(@"C:\", limit: 10);
            Assert.AreEqual(1, snapshots.Count);
            Assert.AreEqual(100L, snapshots[0].LogicalBytes);
            Assert.AreEqual(1, snapshots[0].Categories.Count);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void DeltaHandlesMissingCategoriesAndUnknownPhysicalAllocation()
    {
        var older = new StorageHistorySnapshot(
            1,
            @"C:\Data",
            new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero),
            500,
            640,
            3,
            0,
            2,
            [
                new StorageHistoryCategorySnapshot(StorageFileCategory.Images, 300, 384, 2, 0, 1),
                new StorageHistoryCategorySnapshot(StorageFileCategory.Documents, 200, 256, 1, 0, 1),
            ]);
        var newer = new StorageHistorySnapshot(
            2,
            @"c:\data\",
            new DateTimeOffset(2026, 8, 8, 2, 0, 0, TimeSpan.Zero),
            900,
            null,
            5,
            1,
            3,
            [
                new StorageHistoryCategorySnapshot(StorageFileCategory.Images, 350, null, 3, 1, 1),
                new StorageHistoryCategorySnapshot(StorageFileCategory.Archives, 550, 640, 2, 0, 2),
            ]);

        var delta = StorageHistoryDelta.Between(older, newer);

        Assert.AreEqual(400L, delta.LogicalBytesDelta);
        Assert.IsNull(delta.AllocatedBytesDelta);
        Assert.AreEqual(2L, delta.FileCountDelta);
        Assert.AreEqual(1L, delta.HardLinkAliasCountDelta);
        Assert.AreEqual(1L, delta.TypeCountDelta);

        var images = delta.Categories.Single(item => item.Category == StorageFileCategory.Images);
        Assert.AreEqual(50L, images.LogicalBytesDelta);
        Assert.IsNull(images.AllocatedBytesDelta);

        var documents = delta.Categories.Single(item => item.Category == StorageFileCategory.Documents);
        Assert.AreEqual(-200L, documents.LogicalBytesDelta);
        Assert.AreEqual(-256L, documents.AllocatedBytesDelta);

        var archives = delta.Categories.Single(item => item.Category == StorageFileCategory.Archives);
        Assert.AreEqual(550L, archives.LogicalBytesDelta);
        Assert.AreEqual(640L, archives.AllocatedBytesDelta);
    }

    [TestMethod]
    public async Task SaveRejectsCategoryTotalsThatDoNotReconcile()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var history = new SqliteStorageHistoryStore(databasePath);
            var inconsistent = Analysis(
                @"C:\Data",
                100,
                128,
                1,
                0,
                1,
                [Category(StorageFileCategory.Data, 99, 128, 1, 0, 1)]);

            await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
                await history.SaveSnapshotAsync(inconsistent, DateTimeOffset.UtcNow));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public void StoreRejectsUnknownHistorySchemaVersion()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE storage_history_schema_info(
                        id INTEGER PRIMARY KEY CHECK(id = 1),
                        version INTEGER NOT NULL
                    );
                    INSERT INTO storage_history_schema_info(id, version) VALUES (1, 99);
                    """;
                command.ExecuteNonQuery();
            }

            Assert.ThrowsExactly<InvalidDataException>(() => new SqliteStorageHistoryStore(databasePath));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static StorageFileTypeAnalysis Analysis(
        string rootPath,
        long logical,
        long? allocated,
        int files,
        int aliases,
        int typeCount,
        IReadOnlyList<StorageFileCategoryEntry> categories) =>
        new(rootPath, logical, allocated, files, aliases, typeCount, [])
        {
            Categories = categories,
        };

    private static StorageFileCategoryEntry Category(
        StorageFileCategory category,
        long logical,
        long? allocated,
        int files,
        int aliases,
        int typeCount) =>
        new(category, logical, allocated, files, aliases, typeCount);

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"fileop-storage-history-{Guid.NewGuid():N}.sqlite");

    private static void DeleteDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
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
