using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageHistoryUnicodeRootTests
{
    [TestMethod]
    public async Task UnicodeCaseEquivalentRootsShareSnapshotIdentity()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            using var history = new SqliteStorageHistoryStore(databasePath);
            var capturedAt = new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero);

            var firstId = await history.SaveSnapshotAsync(
                Analysis(@"C:\Ärea", 100, 128),
                capturedAt);
            var replacementId = await history.SaveSnapshotAsync(
                Analysis(@"c:\ärea\", 200, 256),
                capturedAt);

            Assert.AreEqual(firstId, replacementId);

            var snapshots = await history.GetSnapshotsAsync(@"C:\ÄREA", limit: 10);
            Assert.AreEqual(1, snapshots.Count);
            Assert.AreEqual(200L, snapshots[0].LogicalBytes);
            Assert.AreEqual(256L, snapshots[0].AllocatedBytes);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static StorageFileTypeAnalysis Analysis(string rootPath, long logical, long allocated) =>
        new(rootPath, logical, allocated, 1, 0, 1, [])
        {
            Categories =
            [
                new StorageFileCategoryEntry(
                    StorageFileCategory.Data,
                    logical,
                    allocated,
                    1,
                    0,
                    1),
            ],
        };

    private static string CreateDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"fileop-storage-history-unicode-{Guid.NewGuid():N}.sqlite");

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