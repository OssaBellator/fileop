using FileOp.Core.Models;
using FileOp.Core.Performance;
using FileOp.Core.Search;
using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexDatabaseDiagnosticsTests
{
    [TestMethod]
    public async Task ReaderReportsHelperFileAndPageEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fileop-index-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "index.sqlite");
        try
        {
            using (var index = new SqliteFileIndex(databasePath))
            {
                await index.AddBatchAsync([
                    new FileRecord(
                        Path.Combine(directory, "sample.bin"),
                        "sample.bin",
                        directory,
                        ".bin",
                        4_096,
                        false,
                        new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
                        FileAttributes.Normal),
                ]);
            }

            var capturedAt = new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.Zero);
            var diagnostics = await new SqliteIndexDatabaseDiagnosticsReader(databasePath)
                .ReadAsync(capturedAt);

            Assert.AreEqual(capturedAt, diagnostics.CapturedAt);
            Assert.AreEqual(1, diagnostics.IndexedItemCount);
            Assert.IsTrue(diagnostics.DatabaseFileBytes > 0);
            Assert.IsTrue(diagnostics.FileFootprintBytes >= diagnostics.DatabaseFileBytes);
            Assert.IsTrue(diagnostics.PageSizeBytes > 0);
            Assert.IsTrue(diagnostics.PageCount > 0);
            Assert.IsTrue(diagnostics.FreePageCount >= 0);
            Assert.IsTrue(diagnostics.FreePageCount <= diagnostics.PageCount);
            Assert.IsTrue(diagnostics.LivePageBytes >= 0);
            Assert.IsTrue(diagnostics.ConfiguredCacheTargetBytes is > 0);
            Assert.AreEqual("wal", diagnostics.JournalMode, ignoreCase: true);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void DerivedMetricsStayConservativeAndDoNotClaimResidentCache()
    {
        var diagnostics = new IndexDatabaseDiagnostics(
            DateTimeOffset.UnixEpoch,
            10,
            DatabaseFileBytes: 1_000,
            WalFileBytes: 200,
            SharedMemoryFileBytes: 50,
            PageSizeBytes: 4_096,
            PageCount: 100,
            FreePageCount: 25,
            CacheSizeSetting: -2_000,
            JournalMode: "wal");

        Assert.AreEqual(1_250L, diagnostics.FileFootprintBytes);
        Assert.AreEqual(409_600L, diagnostics.LogicalDatabasePageBytes);
        Assert.AreEqual(102_400L, diagnostics.ReusableFreePageBytes);
        Assert.AreEqual(307_200L, diagnostics.LivePageBytes);
        Assert.AreEqual(25d, diagnostics.ReusableFreePagePercent);
        Assert.AreEqual(2_048_000L, diagnostics.ConfiguredCacheTargetBytes);
    }

    [TestMethod]
    public void SharedResolverPreservesHistoricalDatabaseKeyFormat()
    {
        var key = IndexDatabasePathResolver.CreateKey(0x1234UL, @"C:\");
        Assert.AreEqual("ntfs-0000000000001234-c", key);

        var path = IndexDatabasePathResolver.CreatePath(@"C:\Index", 0x1234UL, @"C:\");
        Assert.AreEqual(
            Path.Combine(Path.GetFullPath(@"C:\Index"), "ntfs-0000000000001234-c.sqlite"),
            path);
    }
}
