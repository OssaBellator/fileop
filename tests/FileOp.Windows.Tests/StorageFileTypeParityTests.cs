using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageFileTypeParityTests
{
    [TestMethod]
    public async Task SqliteAndInMemoryFileTypeAnalysisMatch()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"fileop-storage-type-parity-{Guid.NewGuid():N}.sqlite");

        try
        {
            var identity = new FileIdentity(0xCAFE, 0x4242);
            IReadOnlyList<FileRecord> fixture =
            [
                Directory(@"C:\Data\Nested", @"C:\Data", "Nested"),
                File(@"C:\Data\Nested\photo.JPG", @"C:\Data\Nested", "photo.JPG", 200, 256),
                File(@"C:\Data\notes.txt", @"C:\Data", "notes.txt", 100, null),
                File(@"C:\Data\README", @"C:\Data", "README", 25, 32),
                Directory(@"C:\Data\A", @"C:\Data", "A"),
                Directory(@"C:\Data\B", @"C:\Data", "B"),
                File(@"C:\Data\A\shared.bin", @"C:\Data\A", "shared.bin", 300, 384, identity),
                File(@"C:\Data\B\shared.dat", @"C:\Data\B", "shared.dat", 300, 384, identity),
            ];

            using var sqliteIndex = new SqliteFileIndex(databasePath);
            await sqliteIndex.AddBatchAsync(fixture);
            using var sqliteAnalytics = new SqliteStorageAnalytics(databasePath);

            using var memory = new InMemoryFileIndex();
            await memory.AddBatchAsync(fixture);

            var sqlite = await sqliteAnalytics.AnalyzeFileTypesAsync(@"C:\Data", maxTypes: 128);
            var fallback = await memory.AnalyzeFileTypesAsync(@"C:\Data", maxTypes: 128);

            Assert.AreEqual(sqlite.RootPath, fallback.RootPath);
            Assert.AreEqual(sqlite.LogicalBytes, fallback.LogicalBytes);
            Assert.AreEqual(sqlite.AllocatedBytes, fallback.AllocatedBytes);
            Assert.AreEqual(sqlite.FileCount, fallback.FileCount);
            Assert.AreEqual(sqlite.UniqueFileCount, fallback.UniqueFileCount);
            Assert.AreEqual(sqlite.HardLinkAliasCount, fallback.HardLinkAliasCount);
            Assert.AreEqual(sqlite.TypeCount, fallback.TypeCount);
            Assert.AreEqual(sqlite.Types.Count, fallback.Types.Count);

            for (var index = 0; index < sqlite.Types.Count; index++)
            {
                Assert.AreEqual(sqlite.Types[index], fallback.Types[index]);
            }
        }
        finally
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
