using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingStorageOptimizationProtocolTests
{
    [TestMethod]
    public async Task DispatcherNormalizesOptimizationPathsBeforeBackendCall()
    {
        using var backend = new FakeBackend
        {
            Response = new IndexingStorageOptimizationResponse(Analysis(@"C:\Data")),
        };
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion,
            Guid.NewGuid(),
            IndexingServiceOperation.AnalyzeStorageOptimization,
            System.Text.Json.JsonSerializer.SerializeToElement(
                new IndexingStorageOptimizationRequest(0x1234, @"C:\", @"C:\Data\.")));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(response.Success);
        Assert.AreEqual(1, backend.Calls);
        Assert.AreEqual(Path.GetFullPath(@"C:\Data\."), backend.LastRequest?.DirectoryPath);
    }

    [TestMethod]
    public async Task NamedPipeRoundTripPreservesOptimizationEvidence()
    {
        var analysis = Analysis(@"C:\Data");
        using var backend = new FakeBackend
        {
            Response = new IndexingStorageOptimizationResponse(analysis),
        };
        var pipeName = $"fileop-optimize-{Guid.NewGuid():N}";
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            var response = await client.AnalyzeStorageOptimizationAsync(
                new IndexingStorageOptimizationRequest(0x1234, @"C:\", @"C:\Data"),
                cancellation.Token);

            Assert.AreEqual(1, response.Analysis.LargestFiles.Count);
            Assert.AreEqual(1, response.Analysis.StaleLargeFiles.Count);
            Assert.AreEqual(1, response.Analysis.SameSizeCandidateGroups.Count);
            Assert.AreEqual(700L, response.Analysis.SameSizePotentialLogicalSavingsUpperBound);
            Assert.AreEqual(2, response.Analysis.SameSizeCandidateGroups[0].CandidateFileCount);
        }

        await serverTask.WaitAsync(cancellation.Token);
        Assert.AreEqual(1, backend.Calls);
    }

    private static StorageOptimizationAnalysis Analysis(string root) =>
        new(
            root,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            new StorageOptimizationPolicy(
                LargeFileMinimumBytes: 1,
                SameSizeMinimumBytes: 1,
                StaleAgeDays: 1,
                MaxLargeFiles: 10,
                MaxStaleLargeFiles: 10,
                MaxSameSizeGroups: 10,
                MaxFilesPerSameSizeGroup: 10),
            [new StorageOptimizationFileCandidate(
                @"C:\Data\old.bin",
                "old.bin",
                ".bin",
                StorageFileCategory.Data,
                1_000,
                900,
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))],
            [new StorageOptimizationFileCandidate(
                @"C:\Data\old.bin",
                "old.bin",
                ".bin",
                StorageFileCategory.Data,
                1_000,
                900,
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero))],
            [new StorageSameSizeCandidateGroup(
                700,
                2,
                700,
                [
                    new StorageSameSizeCandidateFile(
                        @"C:\Data\copy-a.zip",
                        "copy-a.zip",
                        ".zip",
                        StorageFileCategory.Archives,
                        700,
                        700,
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
                    new StorageSameSizeCandidateFile(
                        @"C:\Data\copy-b.zip",
                        "copy-b.zip",
                        ".zip",
                        StorageFileCategory.Archives,
                        700,
                        700,
                        new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
                ])]);

    private sealed class FakeBackend : IIndexingServiceBackend
    {
        public int Calls { get; private set; }
        public IndexingStorageOptimizationRequest? LastRequest { get; private set; }
        public IndexingStorageOptimizationResponse? Response { get; init; }

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

        public ValueTask<IndexingStorageOptimizationResponse> AnalyzeStorageOptimizationAsync(
            IndexingStorageOptimizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return ValueTask.FromResult(Response ?? throw new InvalidOperationException());
        }

        public void Dispose()
        {
        }
    }
}
