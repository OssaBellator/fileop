using FileOp.Core.Indexing.Service;
using FileOp.Core.Performance;
using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingIndexDiagnosticsProtocolTests
{
    [TestMethod]
    public async Task DispatcherNormalizesIndexDiagnosticsRootBeforeBackendCall()
    {
        using var backend = new FakeBackend
        {
            Response = new IndexingIndexDiagnosticsResponse(Diagnostics()),
        };
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion,
            Guid.NewGuid(),
            IndexingServiceOperation.GetIndexDiagnostics,
            System.Text.Json.JsonSerializer.SerializeToElement(
                new IndexingIndexDiagnosticsRequest(0x1234, @"C:\.")));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(response.Success);
        Assert.AreEqual(1, backend.Calls);
        Assert.AreEqual(Path.GetFullPath(@"C:\."), backend.LastRequest?.VolumeRootPath);
    }

    [TestMethod]
    public async Task NamedPipeRoundTripPreservesIndexDatabaseAndJournalEvidence()
    {
        var diagnostics = Diagnostics();
        using var backend = new FakeBackend
        {
            Response = new IndexingIndexDiagnosticsResponse(diagnostics),
        };
        var pipeName = $"fileop-index-diag-{Guid.NewGuid():N}";
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            var response = await client.GetIndexDiagnosticsAsync(
                new IndexingIndexDiagnosticsRequest(0x1234, @"C:\"),
                cancellation.Token);

            Assert.AreEqual(42, response.Diagnostics.IndexedItemCount);
            Assert.AreEqual(1_250L, response.Diagnostics.FileFootprintBytes);
            Assert.AreEqual(102_400L, response.Diagnostics.ReusableFreePageBytes);
            Assert.AreEqual(2_048_000L, response.Diagnostics.ReaderCacheDefaultTargetBytes);
            Assert.AreEqual("wal", response.Diagnostics.JournalMode);
            Assert.IsNotNull(response.Diagnostics.DurableCheckpoint);
            Assert.AreEqual(7UL, response.Diagnostics.DurableCheckpoint.JournalId);
            Assert.AreEqual(900L, response.Diagnostics.DurableCheckpoint.NextUsn);
            Assert.IsNotNull(response.Diagnostics.JournalFreshness);
            Assert.IsTrue(response.Diagnostics.JournalFreshness.JournalIdentityMatches);
            Assert.AreEqual(100L, response.Diagnostics.JournalFreshness.BacklogUsnDistance);
            Assert.AreEqual(400L, response.Diagnostics.JournalFreshness.RetentionHeadroomUsnDistance);
        }

        await serverTask.WaitAsync(cancellation.Token);
        Assert.AreEqual(1, backend.Calls);
    }

    private static IndexDatabaseDiagnostics Diagnostics()
    {
        var updatedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        return new IndexDatabaseDiagnostics(
            updatedAt.AddMinutes(30),
            42,
            DatabaseFileBytes: 1_000,
            WalFileBytes: 200,
            SharedMemoryFileBytes: 50,
            PageSizeBytes: 4_096,
            PageCount: 100,
            FreePageCount: 25,
            CacheSizeSetting: -2_000,
            JournalMode: "wal",
            DurableCheckpoint: new IndexJournalCheckpointDiagnostics(7, 900, updatedAt),
            JournalFreshness: new IndexJournalFreshnessDiagnostics(
                7,
                900,
                updatedAt,
                7,
                500,
                1_000));
    }

    private sealed class FakeBackend : IIndexingServiceBackend
    {
        public int Calls { get; private set; }
        public IndexingIndexDiagnosticsRequest? LastRequest { get; private set; }
        public IndexingIndexDiagnosticsResponse? Response { get; init; }

        public ValueTask<IndexingHelloResponse> HelloAsync(
            IndexingHelloRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingHelloResponse(
                IndexingServiceProtocol.CurrentVersion,
                "FakeIndexer",
                "1.0.0",
                false));

        public ValueTask<IndexingGetVolumesResponse> GetVolumesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingGetVolumesResponse([]));

        public ValueTask<IndexingServiceStatusResponse> GetStatusAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingServiceStatusResponse(0, 0, 0, []));

        public ValueTask<IndexingIndexDiagnosticsResponse> GetIndexDiagnosticsAsync(
            IndexingIndexDiagnosticsRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return ValueTask.FromResult(Response ?? throw new InvalidOperationException());
        }

        public ValueTask<IndexingVolumeOperationResponse> RebuildVolumeAsync(
            IndexingVolumeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IndexingVolumeOperationResponse>(new NotSupportedException());

        public ValueTask<IndexingVolumeOperationResponse> SyncVolumeAsync(
            IndexingVolumeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IndexingVolumeOperationResponse>(new NotSupportedException());

        public ValueTask<IndexingSearchResponse> SearchAsync(
            IndexingSearchRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingSearchResponse([]));

        public void Dispose()
        {
        }
    }
}
