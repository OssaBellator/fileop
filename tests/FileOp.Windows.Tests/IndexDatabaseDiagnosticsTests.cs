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
            Assert.IsTrue(diagnostics.ReaderCacheDefaultTargetBytes is > 0);
            Assert.IsTrue(string.Equals("wal", diagnostics.JournalMode, StringComparison.OrdinalIgnoreCase));
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
    public async Task ReaderPreservesDurableCheckpointIdentityPositionAndTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fileop-index-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "index.sqlite");
        const string sourceKey = "ntfs:checkpoint-test";
        const ulong journalId = 0xFEDCBA9876543210UL;
        const long nextUsn = 987_654_321;
        var updatedAt = new DateTimeOffset(2026, 8, 10, 2, 3, 4, TimeSpan.Zero);
        var capturedAt = updatedAt.AddMinutes(30);

        try
        {
            using (var index = new SqliteFileIndex(databasePath))
            {
                await index.SaveCheckpointAsync(new IndexSourceCheckpoint(
                    sourceKey,
                    journalId,
                    nextUsn,
                    updatedAt));
            }

            var diagnostics = await new SqliteIndexDatabaseDiagnosticsReader(databasePath)
                .ReadWithCheckpointAsync(sourceKey, capturedAt);

            Assert.IsNotNull(diagnostics.DurableCheckpoint);
            Assert.AreEqual(journalId, diagnostics.DurableCheckpoint.JournalId);
            Assert.AreEqual(nextUsn, diagnostics.DurableCheckpoint.NextUsn);
            Assert.AreEqual(updatedAt, diagnostics.DurableCheckpoint.UpdatedAt);
            Assert.AreEqual(capturedAt, diagnostics.CapturedAt);
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
    public void FreshnessReportsReadableWindowAsUsnDistances()
    {
        var updatedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var freshness = new IndexJournalFreshnessDiagnostics(
            7,
            900,
            updatedAt,
            7,
            500,
            1_000);

        Assert.IsTrue(freshness.JournalIdentityMatches);
        Assert.IsTrue(freshness.CheckpointWithinReadableWindow);
        Assert.IsFalse(freshness.CheckpointBelowRetentionFloor);
        Assert.IsFalse(freshness.CheckpointAheadOfJournal);
        Assert.AreEqual(100L, freshness.BacklogUsnDistance);
        Assert.AreEqual(400L, freshness.RetentionHeadroomUsnDistance);
        Assert.AreEqual(TimeSpan.FromMinutes(30), freshness.AgeAt(updatedAt.AddMinutes(30)));
    }

    [TestMethod]
    public void FreshnessDoesNotInventBacklogForInvalidJournalContinuity()
    {
        var updatedAt = DateTimeOffset.UnixEpoch;
        var changed = new IndexJournalFreshnessDiagnostics(1, 900, updatedAt, 2, 500, 1_000);
        var expired = new IndexJournalFreshnessDiagnostics(1, 400, updatedAt, 1, 500, 1_000);
        var ahead = new IndexJournalFreshnessDiagnostics(1, 1_100, updatedAt, 1, 500, 1_000);

        Assert.IsFalse(changed.JournalIdentityMatches);
        Assert.IsNull(changed.BacklogUsnDistance);
        Assert.IsNull(changed.RetentionHeadroomUsnDistance);

        Assert.IsTrue(expired.CheckpointBelowRetentionFloor);
        Assert.IsNull(expired.BacklogUsnDistance);
        Assert.IsNull(expired.RetentionHeadroomUsnDistance);

        Assert.IsTrue(ahead.CheckpointAheadOfJournal);
        Assert.IsNull(ahead.BacklogUsnDistance);
        Assert.IsNull(ahead.RetentionHeadroomUsnDistance);
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
        Assert.AreEqual(2_048_000L, diagnostics.ReaderCacheDefaultTargetBytes);
    }

    [TestMethod]
    public void DerivedFileFootprintClampsMalformedNegativeInputs()
    {
        var diagnostics = new IndexDatabaseDiagnostics(
            DateTimeOffset.UnixEpoch,
            0,
            DatabaseFileBytes: -1,
            WalFileBytes: 20,
            SharedMemoryFileBytes: -30,
            PageSizeBytes: 4_096,
            PageCount: 0,
            FreePageCount: 0,
            CacheSizeSetting: 0,
            JournalMode: string.Empty);

        Assert.AreEqual(20L, diagnostics.FileFootprintBytes);
        Assert.IsNull(diagnostics.ReusableFreePagePercent);
        Assert.IsNull(diagnostics.ReaderCacheDefaultTargetBytes);
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
