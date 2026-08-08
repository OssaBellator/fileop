using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingStorageProtocolTests
{
    [TestMethod]
    public async Task NamedPipeRoundTripReturnsTypedStorageAnalysis()
    {
        var pipeName = $"fileop-storage-test-{Guid.NewGuid():N}";
        using var backend = new StorageBackend();
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            var response = await client.AnalyzeStorageAsync(
                new IndexingStorageAnalysisRequest(
                    0x1234,
                    @"C:\Folder\..",
                    @"C:\Data\Nested\..",
                    MaxEntries: 32),
                cancellation.Token);

            Assert.AreEqual(@"C:\Data", response.Analysis.RootPath);
            Assert.AreEqual(300L, response.Analysis.LogicalBytes);
            Assert.AreEqual(384L, response.Analysis.AllocatedBytes);
            Assert.AreEqual(2, response.Analysis.UniqueFileCount);
            Assert.AreEqual(0, response.Analysis.HardLinkAliasCount);
            Assert.AreEqual(1, response.Analysis.Entries.Count);
            Assert.AreEqual("Alpha", response.Analysis.Entries[0].Name);
            Assert.AreEqual(@"C:\", backend.LastRequest?.VolumeRootPath);
            Assert.AreEqual(@"C:\Data", backend.LastRequest?.DirectoryPath);
            Assert.AreEqual(32, backend.LastRequest?.MaxEntries);
        }

        await serverTask.WaitAsync(cancellation.Token);
    }

    private sealed class StorageBackend : IIndexingServiceBackend
    {
        public IndexingStorageAnalysisRequest? LastRequest { get; private set; }

        public ValueTask<IndexingHelloResponse> HelloAsync(
            IndexingHelloRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingHelloResponse(
                IndexingServiceProtocol.CurrentVersion,
                "StorageBackend",
                "1.0",
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
            ValueTask.FromResult(new IndexingVolumeOperationResponse(
                request.VolumeIdentity,
                request.RootPath,
                0,
                0,
                0,
                "Idle"));

        public ValueTask<IndexingVolumeOperationResponse> SyncVolumeAsync(
            IndexingVolumeRequest request,
            CancellationToken cancellationToken = default) =>
            RebuildVolumeAsync(request, cancellationToken);

        public ValueTask<IndexingSearchResponse> SearchAsync(
            IndexingSearchRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingSearchResponse([]));

        public ValueTask<IndexingStorageAnalysisResponse> AnalyzeStorageAsync(
            IndexingStorageAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(new IndexingStorageAnalysisResponse(
                new StorageDirectoryAnalysis(
                    request.DirectoryPath,
                    300,
                    384,
                    2,
                    1,
                    0,
                    1,
                    [new StorageDirectoryEntry(
                        @"C:\Data\Alpha",
                        "Alpha",
                        true,
                        300,
                        384,
                        2,
                        1,
                        0)])));
        }

        public void Dispose()
        {
        }
    }
}
