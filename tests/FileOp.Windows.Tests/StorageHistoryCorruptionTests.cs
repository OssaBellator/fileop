using FileOp.Core.Storage;
using Microsoft.Data.Sqlite;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageHistoryCorruptionTests
{
    [TestMethod]
    public async Task ReadRejectsPersistedRootTotalsThatDoNotReconcile()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var history = new SqliteStorageHistoryStore(databasePath);
            await history.SaveSnapshotAsync(
                Analysis(
                    @"C:\Data",
                    100,
                    128,
                    1,
                    0,
                    1,
                    [new StorageFileCategoryEntry(
                        StorageFileCategory.Data,
                        100,
                        128,
                        1,
                        0,
                        1)]),
                new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero));

            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE storage_history_snapshots SET logical_bytes = 999;";
                command.ExecuteNonQuery();
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await history.GetSnapshotsAsync(@"C:\Data", limit: 10));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [TestMethod]
    public async Task ReadRejectsUnknownPersistedCategoryValue()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var history = new SqliteStorageHistoryStore(databasePath);
            await history.SaveSnapshotAsync(
                Analysis(
                    @"C:\Data",
                    100,
                    128,
                    1,
                    0,
                    1,
                    [new StorageFileCategoryEntry(
                        StorageFileCategory.Data,
                        100,
                        128,
                        1,
                        0,
                        1)]),
                new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero));

            using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE storage_history_categories SET category = 999;";
                command.ExecuteNonQuery();
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await history.GetSnapshotsAsync(@"C:\Data", limit: 10));
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

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"fileop-storage-history-corrupt-{Guid.NewGuid():N}.sqlite");

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
