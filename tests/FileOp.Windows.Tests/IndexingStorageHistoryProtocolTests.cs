using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingStorageHistoryProtocolTests
{
    [TestMethod]
    public void CapturePolicyUsesUtcHourlyBuckets()
    {
        var local = new DateTimeOffset(2026, 8, 8, 16, 22, 47, TimeSpan.FromHours(10));

        var bucket = StorageHistoryCapturePolicy.GetHourlyBucket(local);

        Assert.AreEqual(new DateTimeOffset(2026, 8, 8, 6, 0, 0, TimeSpan.Zero), bucket);
    }

    [TestMethod]
    public async Task DispatcherRejectsInvalidHistoryLimitBeforeBackendCall()
    {
        using var backend = new FakeBackend();
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion,
            Guid.NewGuid(),
            IndexingServiceOperation.GetStorageHistory,
            System.Text.Json.JsonSerializer.SerializeToElement(new IndexingStorageHistoryQueryRequest(
                0x1234,
                @"C:\",
                @"C:\Data",
                Limit: 0)));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(IndexingServiceErrorCode.InvalidRequest, response.Error?.Code);
        Assert.AreEqual(0, backend.HistoryQueryCalls);
    }

    [TestMethod]
    public async Task NamedPipeRoundTripPreservesCaptureAndHistoryPayloads()
    {
        var captured = new StorageHistorySnapshot(
            42,
            @"C:\Data",
            new DateTimeOffset(2026, 8, 8, 6, 0, 0, TimeSpan.Zero),
            300,
            256,
            2,
            1,
            2,
            [
                new StorageHistoryCategorySnapshot(StorageFileCategory.Data, 200, 256, 1, 0, 1),
                new StorageHistoryCategorySnapshot(StorageFileCategory.Images, 100, 0, 1, 1, 1),
            ]);
        using var backend = new FakeBackend
        {
            CaptureResponse = new IndexingStorageHistoryCaptureResponse(captured),
            QueryResponse = new IndexingStorageHistoryQueryResponse([captured]),
        };
        var pipeName = $"fileop-history-{Guid.NewGuid():N}";
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            var capture = await client.CaptureStorageHistoryAsync(
                new IndexingStorageHistoryCaptureRequest(0x1234, @"C:\", @"C:\Data"),
                cancellation.Token);
            Assert.AreEqual(42L, capture.Snapshot.Id);
            Assert.AreEqual(2, capture.Snapshot.Categories.Count);

            var history = await client.GetStorageHistoryAsync(
                new IndexingStorageHistoryQueryRequest(0x1234, @"C:\", @"C:\Data", 30),
                cancellation.Token);
            Assert.AreEqual(1, history.Snapshots.Count);
            Assert.AreEqual(256L, history.Snapshots[0].AllocatedBytes);
        }

        await serverTask.WaitAsync(cancellation.Token);
        Assert.AreEqual(1, backend.CaptureCalls);
        Assert.AreEqual(1, backend.HistoryQueryCalls);
        Assert.AreEqual(@"C:\Data", backend.LastCaptureRequest?.DirectoryPath);
        Assert.AreEqual(30, backend.LastQueryRequest?.Limit);
    }

    private sealed class FakeBackend : IIndexingServiceBackend
    {
        public int CaptureCalls { get; private set; }
        public int HistoryQueryCalls { get; private set; }
        public IndexingStorageHistoryCaptureRequest? LastCaptureRequest { get; private set; }
        public IndexingStorageHistoryQueryRequest? LastQueryRequest { get; private set; }
        public IndexingStorageHistoryCaptureResponse? CaptureResponse { get; init; }
        public IndexingStorageHistoryQueryResponse? QueryResponse { get; init; }

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

        public ValueTask<IndexingStorageHistoryCaptureResponse> CaptureStorageHistoryAsync(
            IndexingStorageHistoryCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            LastCaptureRequest = request;
            return ValueTask.FromResult(CaptureResponse ?? throw new InvalidOperationException());
        }

        public ValueTask<IndexingStorageHistoryQueryResponse> GetStorageHistoryAsync(
            IndexingStorageHistoryQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            HistoryQueryCalls++;
            LastQueryRequest = request;
            return ValueTask.FromResult(QueryResponse ?? throw new InvalidOperationException());
        }

        public void Dispose()
        {
        }
    }
}