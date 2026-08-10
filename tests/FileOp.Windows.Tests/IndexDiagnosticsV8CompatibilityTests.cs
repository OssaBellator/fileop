using System.Text.Json;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexDiagnosticsV8CompatibilityTests
{
    [TestMethod]
    public void NewClientAcceptsLegacyV8DiagnosticsWithoutFreshnessFields()
    {
        const string json = """
            {
              "diagnostics": {
                "capturedAt": "2026-08-10T00:00:00+00:00",
                "indexedItemCount": 42,
                "databaseFileBytes": 1000,
                "walFileBytes": 200,
                "sharedMemoryFileBytes": 50,
                "pageSizeBytes": 4096,
                "pageCount": 100,
                "freePageCount": 25,
                "cacheSizeSetting": -2000,
                "journalMode": "wal"
              }
            }
            """;

        var response = JsonSerializer.Deserialize<IndexingIndexDiagnosticsResponse>(
            json,
            IndexingServiceJson.SerializerOptions);

        Assert.IsNotNull(response);
        Assert.AreEqual(42, response.Diagnostics.IndexedItemCount);
        Assert.IsNull(response.Diagnostics.DurableCheckpoint);
        Assert.IsNull(response.Diagnostics.JournalFreshness);
    }

    [TestMethod]
    public void LegacyShapeIgnoresNewV8FreshnessProperties()
    {
        var updatedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var response = new IndexingIndexDiagnosticsResponse(new IndexDatabaseDiagnostics(
            updatedAt.AddMinutes(30),
            42,
            1_000,
            200,
            50,
            4_096,
            100,
            25,
            -2_000,
            "wal",
            new IndexJournalCheckpointDiagnostics(7, 900, updatedAt),
            new IndexJournalFreshnessDiagnostics(7, 900, updatedAt, 7, 500, 1_000)));
        var json = JsonSerializer.Serialize(response, IndexingServiceJson.SerializerOptions);

        var legacy = JsonSerializer.Deserialize<LegacyIndexDiagnosticsResponse>(
            json,
            IndexingServiceJson.SerializerOptions);

        Assert.IsNotNull(legacy);
        Assert.AreEqual(42, legacy.Diagnostics.IndexedItemCount);
        Assert.AreEqual("wal", legacy.Diagnostics.JournalMode);
    }

    public sealed record LegacyIndexDiagnosticsResponse(LegacyIndexDatabaseDiagnostics Diagnostics);

    public sealed record LegacyIndexDatabaseDiagnostics(
        DateTimeOffset CapturedAt,
        int IndexedItemCount,
        long DatabaseFileBytes,
        long WalFileBytes,
        long SharedMemoryFileBytes,
        long PageSizeBytes,
        long PageCount,
        long FreePageCount,
        long CacheSizeSetting,
        string JournalMode);
}
