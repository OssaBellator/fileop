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

    [TestMethod]
    public async Task NamedPipeRoundTripReturnsTypedStorageFileTypesAndExactCategories()
    {
        var pipeName = $"fileop-storage-types-test-{Guid.NewGuid():N}";
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
            var response = await client.AnalyzeStorageTypesAsync(
                new IndexingStorageFileTypeRequest(
                    0x1234,
                    @"C:\Folder\..",
                    @"C:\Data\Nested\..",
                    MaxTypes: 24),
                cancellation.Token);

            Assert.AreEqual(@"C:\Data", response.Analysis.RootPath);
            Assert.AreEqual(300L, response.Analysis.LogicalBytes);
            Assert.AreEqual(384L, response.Analysis.AllocatedBytes);
            Assert.AreEqual(2, response.Analysis.FileCount);
            Assert.AreEqual(2, response.Analysis.UniqueFileCount);
            Assert.AreEqual(1, response.Analysis.TypeCount);
            Assert.AreEqual("jpg", response.Analysis.Types[0].Extension);
            Assert.AreEqual(StorageFileCategory.Images, response.Analysis.Types[0].Category);
            Assert.AreEqual(1, response.Analysis.Categories.Count);
            Assert.AreEqual(StorageFileCategory.Images, response.Analysis.Categories[0].Category);
            Assert.AreEqual(300L, response.Analysis.Categories[0].LogicalBytes);
            Assert.AreEqual(384L, response.Analysis.Categories[0].AllocatedBytes);
            Assert.AreEqual(2, response.Analysis.Categories[0].FileCount);
            Assert.AreEqual(1, response.Analysis.Categories[0].TypeCount);
            Assert.AreEqual(@"C:\", backend.LastTypeRequest?.VolumeRootPath);
            Assert.AreEqual(@"C:\Data", backend.LastTypeRequest?.DirectoryPath);
            Assert.AreEqual(24, backend.LastTypeRequest?.MaxTypes);
        }

        await serverTask.WaitAsync(cancellation.Token);
    }

    private sealed class StorageBackend : IIndexingServiceBackend
    {
        public IndexingStorageAnalysisRequest? LastRequest { get; private set; }

        public IndexingStorageFileTypeRequest? LastTypeRequest { get; private set; }

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

        public ValueTask<IndexingStorageFileTypeResponse> AnalyzeStorageTypesAsync(
            IndexingStorageFileTypeRequest request,
            CancellationToken cancellationToken = default)
        {
            LastTypeRequest = request;
            var analysis = new StorageFileTypeAnalysis(
                request.DirectoryPath,
                300,
                384,
                2,
                0,
                1,
                [new StorageFileTypeEntry(
                    "jpg",
                    StorageFileCategory.Images,
                    300,
                    384,
                    2,
                    0)])
            {
                Categories =
                [
                    new StorageFileCategoryEntry(
                        StorageFileCategory.Images,
                        300,
                        384,
                        2,
                        0,
                        1),
                ],
            };
            return ValueTask.FromResult(new IndexingStorageFileTypeResponse(analysis));
        }

        public void Dispose()
        {
        }
    }
}
