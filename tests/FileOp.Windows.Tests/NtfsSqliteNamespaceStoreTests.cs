using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Windows.Ntfs;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class NtfsSqliteNamespaceStoreTests
{
    [TestMethod]
    public async Task ApplyAsync_MoveDirectory_RewritesDescendantsAndCheckpoint()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"fileop-test-{Guid.NewGuid():N}.db");

        try
        {
            var volume = 0x1234UL;
            var rootIdentity = new FileIdentity(volume, 1);
            var directoryIdentity = new FileIdentity(volume, 2);
            var childIdentity = new FileIdentity(volume, 3);

            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync([
                Record(@"C:\", "C:", string.Empty, true, rootIdentity, rootIdentity),
                Record(@"C:\Old", "Old", @"C:\", true, directoryIdentity, rootIdentity),
                Record(@"C:\Old\Child.txt", "Child.txt", @"C:\Old", false, childIdentity, directoryIdentity, 42),
            ]);

            using var store = new NtfsSqliteNamespaceStore(databasePath);
            var renamedDirectory = Record(
                @"C:\New",
                "New",
                @"C:\",
                true,
                directoryIdentity,
                rootIdentity);

            await store.ApplyAsync(
                [NtfsIndexMutation.Move(@"C:\Old", renamedDirectory)],
                "ntfs:0000000000001234",
                new NtfsJournalCheckpoint(77, 900));

            var childRows = await store.FindByIdentityAsync(childIdentity);
            Assert.AreEqual(1, childRows.Count);
            Assert.AreEqual(@"C:\New\Child.txt", childRows[0].Path);
            Assert.AreEqual(@"C:\New", childRows[0].ParentPath);

            var directoryRows = await store.FindByIdentityAsync(directoryIdentity);
            Assert.AreEqual(1, directoryRows.Count);
            Assert.AreEqual(@"C:\New", directoryRows[0].Path);
            Assert.AreEqual("New", directoryRows[0].Name);

            var checkpoint = await index.GetCheckpointAsync("ntfs:0000000000001234");
            Assert.IsNotNull(checkpoint);
            Assert.AreEqual(77UL, checkpoint.Generation);
            Assert.AreEqual(900L, checkpoint.Position);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    [TestMethod]
    public async Task ApplyAsync_ReplayedDirectoryMove_PreservesAlreadyMovedDescendants()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"fileop-test-{Guid.NewGuid():N}.db");

        try
        {
            var volume = 0x5678UL;
            var rootIdentity = new FileIdentity(volume, 1);
            var directoryIdentity = new FileIdentity(volume, 2);
            var childIdentity = new FileIdentity(volume, 3);

            using var index = new SqliteFileIndex(databasePath);
            await index.AddBatchAsync([
                Record(@"C:\", "C:", string.Empty, true, rootIdentity, rootIdentity),
                Record(@"C:\New", "New", @"C:\", true, directoryIdentity, rootIdentity),
                Record(@"C:\New\Child.txt", "Child.txt", @"C:\New", false, childIdentity, directoryIdentity, 42),
            ]);

            using var store = new NtfsSqliteNamespaceStore(databasePath);
            var renamedDirectory = Record(
                @"C:\New",
                "New",
                @"C:\",
                true,
                directoryIdentity,
                rootIdentity);

            await store.ApplyAsync(
                [NtfsIndexMutation.Move(@"C:\Old", renamedDirectory)],
                "ntfs:0000000000005678",
                new NtfsJournalCheckpoint(88, 950));

            var childRows = await store.FindByIdentityAsync(childIdentity);
            Assert.AreEqual(1, childRows.Count);
            Assert.AreEqual(@"C:\New\Child.txt", childRows[0].Path);
            Assert.AreEqual(@"C:\New", childRows[0].ParentPath);

            var directoryRows = await store.FindByIdentityAsync(directoryIdentity);
            Assert.AreEqual(1, directoryRows.Count);
            Assert.AreEqual(@"C:\New", directoryRows[0].Path);

            var checkpoint = await index.GetCheckpointAsync("ntfs:0000000000005678");
            Assert.IsNotNull(checkpoint);
            Assert.AreEqual(88UL, checkpoint.Generation);
            Assert.AreEqual(950L, checkpoint.Position);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(databasePath);
        }
    }

    private static FileRecord Record(
        string path,
        string name,
        string parentPath,
        bool directory,
        FileIdentity identity,
        FileIdentity parentIdentity,
        long length = 0) =>
        new(
            path,
            name,
            parentPath,
            directory ? string.Empty : Path.GetExtension(name),
            length,
            directory,
            DateTimeOffset.UnixEpoch,
            directory ? FileAttributes.Directory : FileAttributes.Normal,
            identity,
            parentIdentity,
            directory ? 0 : length);

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
