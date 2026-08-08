using FileOp.Core.Indexing.Service;
using FileOp.Core.Models;
using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingDirectoryBrowseProtocolTests
{
    [TestMethod]
    public async Task DispatcherRejectsInvalidBrowsePageSizeBeforeBackendCall()
    {
        using var backend = new FakeBackend();
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion,
            Guid.NewGuid(),
            IndexingServiceOperation.BrowseDirectory,
            System.Text.Json.JsonSerializer.SerializeToElement(new IndexingDirectoryBrowseRequest(
                0x1234,
                @"C:\",
                @"C:\Data",
                PageSize: 0)));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(IndexingServiceErrorCode.InvalidRequest, response.Error?.Code);
        Assert.AreEqual(0, backend.BrowseCalls);
    }

    [TestMethod]
    public async Task DispatcherRejectsCursorFromAnotherDirectoryBeforeBackendCall()
    {
        using var backend = new FakeBackend();
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion,
            Guid.NewGuid(),
            IndexingServiceOperation.BrowseDirectory,
            System.Text.Json.JsonSerializer.SerializeToElement(new IndexingDirectoryBrowseRequest(
                0x1234,
                @"C:\",
                @"C:\Data",
                64,
                new FileDirectoryBrowseCursor(false, "outside.txt", @"C:\Other\outside.txt"))));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(IndexingServiceErrorCode.InvalidRequest, response.Error?.Code);
        Assert.AreEqual(0, backend.BrowseCalls);
    }

    [TestMethod]
    public async Task NamedPipeRoundTripPreservesBrowseCursorAndEntries()
    {
        var entry = new IndexingSearchResult(
            @"C:\Data\alpha.txt",
            "alpha.txt",
            @"C:\Data",
            ".txt",
            123,
            128,
            false,
            new DateTimeOffset(2026, 8, 8, 7, 30, 0, TimeSpan.Zero),
            FileAttributes.Archive,
            new FileIdentity(0x1234, 10),
            new FileIdentity(0x1234, 5));
        var next = new FileDirectoryBrowseCursor(false, "alpha.txt", entry.Path);
        using var backend = new FakeBackend
        {
            BrowseResponse = new IndexingDirectoryBrowseResponse(
                @"C:\Data",
                42,
                [entry],
                next),
        };
        var pipeName = $"fileop-browse-{Guid.NewGuid():N}";
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            var response = await client.BrowseDirectoryAsync(
                new IndexingDirectoryBrowseRequest(
                    0x1234,
                    @"C:\",
                    @"C:\Data",
                    64,
                    new FileDirectoryBrowseCursor(true, "Folder", @"C:\Data\Folder")),
                cancellation.Token);

            Assert.AreEqual(42, response.TotalCount);
            Assert.AreEqual(1, response.Entries.Count);
            Assert.AreEqual(entry.Path, response.Entries[0].Path);
            Assert.AreEqual(next, response.NextCursor);
        }

        await serverTask.WaitAsync(cancellation.Token);
        Assert.AreEqual(1, backend.BrowseCalls);
        Assert.AreEqual(64, backend.LastBrowseRequest?.PageSize);
        Assert.AreEqual(@"C:\Data\Folder", backend.LastBrowseRequest?.Cursor?.Path);
    }

    private sealed class FakeBackend : IIndexingServiceBackend
    {
        public int BrowseCalls { get; private set; }
        public IndexingDirectoryBrowseRequest? LastBrowseRequest { get; private set; }
        public IndexingDirectoryBrowseResponse? BrowseResponse { get; init; }

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

        public ValueTask<IndexingDirectoryBrowseResponse> BrowseDirectoryAsync(
            IndexingDirectoryBrowseRequest request,
            CancellationToken cancellationToken = default)
        {
            BrowseCalls++;
            LastBrowseRequest = request;
            return ValueTask.FromResult(BrowseResponse ?? throw new InvalidOperationException());
        }

        public void Dispose()
        {
        }
    }
}
