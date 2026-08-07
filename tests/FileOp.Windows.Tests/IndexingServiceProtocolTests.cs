using System.Text.Json;
using FileOp.Core.Indexing.Service;
using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingServiceProtocolTests
{
    [TestMethod]
    public async Task DispatcherRejectsProtocolMismatchBeforeBackendCall()
    {
        using var backend = new FakeBackend();
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion + 1,
            Guid.NewGuid(),
            IndexingServiceOperation.Hello,
            JsonSerializer.SerializeToElement(new IndexingHelloRequest("test")));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(IndexingServiceErrorCode.ProtocolMismatch, response.Error?.Code);
        Assert.AreEqual(0, backend.HelloCalls);
    }

    [TestMethod]
    public async Task DispatcherRejectsEmptyRequestIdBeforeBackendCall()
    {
        using var backend = new FakeBackend();
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion,
            Guid.Empty,
            IndexingServiceOperation.Hello,
            JsonSerializer.SerializeToElement(new IndexingHelloRequest("test")));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(IndexingServiceErrorCode.InvalidRequest, response.Error?.Code);
        Assert.AreEqual(0, backend.HelloCalls);
    }

    [TestMethod]
    public async Task DispatcherReturnsInvalidRequestForMalformedPayload()
    {
        using var backend = new FakeBackend();
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion,
            Guid.NewGuid(),
            IndexingServiceOperation.RebuildVolume,
            JsonSerializer.SerializeToElement(new { wrong = "shape" }));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(IndexingServiceErrorCode.InvalidRequest, response.Error?.Code);
    }

    [TestMethod]
    public async Task DispatcherPreservesStructuredBackendError()
    {
        using var backend = new FakeBackend
        {
            SearchException = new IndexingServiceException(
                IndexingServiceErrorCode.ElevationRequired,
                "Elevation required.",
                canRetry: true),
        };
        var dispatcher = new IndexingServiceDispatcher(backend);
        var request = new IndexingServiceRequest(
            IndexingServiceProtocol.CurrentVersion,
            Guid.NewGuid(),
            IndexingServiceOperation.Search,
            JsonSerializer.SerializeToElement(new IndexingSearchRequest("test")));

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsFalse(response.Success);
        Assert.AreEqual(IndexingServiceErrorCode.ElevationRequired, response.Error?.Code);
        Assert.IsTrue(response.Error?.CanRetry == true);
    }

    [TestMethod]
    public async Task NamedPipeRoundTripUsesTypedClientAndDispatcher()
    {
        var pipeName = $"fileop-test-{Guid.NewGuid():N}";
        using var backend = new FakeBackend();
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            var hello = await client.HelloAsync("test-client", cancellation.Token);
            Assert.AreEqual(IndexingServiceProtocol.CurrentVersion, hello.ProtocolVersion);
            Assert.AreEqual("FakeIndexer", hello.ServiceName);

            var volumes = await client.GetVolumesAsync(cancellation.Token);
            Assert.AreEqual(1, volumes.Volumes.Count);
            Assert.AreEqual(@"C:\", volumes.Volumes[0].RootPath);
        }

        await serverTask.WaitAsync(cancellation.Token);
    }

    [TestMethod]
    public async Task NamedPipeRoundTripPreservesStructuredRemoteError()
    {
        var pipeName = $"fileop-test-{Guid.NewGuid():N}";
        using var backend = new FakeBackend
        {
            SearchException = new IndexingServiceException(
                IndexingServiceErrorCode.SnapshotRequired,
                "Fresh snapshot required.",
                canRetry: true),
        };
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            var exception = await Assert.ThrowsExactlyAsync<IndexingServiceRemoteException>(async () =>
                await client.SearchAsync(new IndexingSearchRequest("test"), cancellation.Token));
            Assert.AreEqual(IndexingServiceErrorCode.SnapshotRequired, exception.Error.Code);
            Assert.IsTrue(exception.Error.CanRetry);
        }

        await serverTask.WaitAsync(cancellation.Token);
    }

    [TestMethod]
    public async Task CancellingInFlightRequestFaultsClientConnection()
    {
        var pipeName = $"fileop-test-{Guid.NewGuid():N}";
        using var backend = new FakeBackend
        {
            SearchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            SearchRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            using var requestCancellation = new CancellationTokenSource();
            var searchTask = client.SearchAsync(
                new IndexingSearchRequest("test"),
                requestCancellation.Token).AsTask();

            await backend.SearchStarted!.Task.WaitAsync(cancellation.Token);
            requestCancellation.Cancel();
            try
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await searchTask);
                Assert.IsFalse(client.IsConnected);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                    await client.HelloAsync("after-cancel", cancellation.Token));
            }
            finally
            {
                backend.SearchRelease!.TrySetResult(true);
            }
        }

        await serverTask.WaitAsync(cancellation.Token);
    }

    [TestMethod]
    public async Task OversizedResponseReturnsRetryableErrorAndKeepsPipeAlive()
    {
        var oversizedPath = new string('x', IndexingServiceProtocol.MaximumFrameBytes + 1024);
        using var backend = new FakeBackend
        {
            SearchResponse = new IndexingSearchResponse(
                [new IndexingSearchResult(
                    oversizedPath,
                    "huge.bin",
                    string.Empty,
                    ".bin",
                    1,
                    1,
                    false,
                    DateTimeOffset.UnixEpoch,
                    FileAttributes.Normal,
                    null,
                    null)]),
        };
        var pipeName = $"fileop-test-{Guid.NewGuid():N}";
        var server = new IndexingPipeServer(
            pipeName,
            Environment.ProcessId,
            new IndexingServiceDispatcher(backend));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = server.RunSingleClientAsync(cancellation.Token);

        await using (var client = new IndexingServiceClient(pipeName))
        {
            await client.ConnectAsync(cancellation.Token);
            var exception = await Assert.ThrowsExactlyAsync<IndexingServiceRemoteException>(async () =>
                await client.SearchAsync(new IndexingSearchRequest("test"), cancellation.Token));
            Assert.AreEqual(IndexingServiceErrorCode.ResponseTooLarge, exception.Error.Code);
            Assert.IsTrue(exception.Error.CanRetry);

            var hello = await client.HelloAsync("after-oversize", cancellation.Token);
            Assert.AreEqual("FakeIndexer", hello.ServiceName);
        }

        await serverTask.WaitAsync(cancellation.Token);
    }

    [TestMethod]
    public async Task IndexerProcessSessionCompletesRealHostHandshake()
    {
        var executablePath = Environment.GetEnvironmentVariable("FILEOP_INDEXER_PATH");
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Assert.Inconclusive("FILEOP_INDEXER_PATH is only set by the Windows CI helper-process smoke test.");
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await IndexingServiceProcessSession.StartAsync(
            executablePath!,
            elevated: false,
            connectTimeout: TimeSpan.FromSeconds(20),
            cancellation.Token);

        var hello = await session.Client.HelloAsync("process-test", cancellation.Token);
        Assert.AreEqual(IndexingServiceProtocol.CurrentVersion, hello.ProtocolVersion);
        Assert.AreEqual("FileOp.Indexer", hello.ServiceName);

        var volumes = await session.Client.GetVolumesAsync(cancellation.Token);
        Assert.IsNotNull(volumes.Volumes);
    }

    private sealed class FakeBackend : IIndexingServiceBackend
    {
        public int HelloCalls { get; private set; }

        public Exception? SearchException { get; init; }

        public IndexingSearchResponse? SearchResponse { get; init; }

        public TaskCompletionSource<bool>? SearchStarted { get; init; }

        public TaskCompletionSource<bool>? SearchRelease { get; init; }

        public ValueTask<IndexingHelloResponse> HelloAsync(
            IndexingHelloRequest request,
            CancellationToken cancellationToken = default)
        {
            HelloCalls++;
            return ValueTask.FromResult(new IndexingHelloResponse(
                IndexingServiceProtocol.CurrentVersion,
                "FakeIndexer",
                "1.0.0",
                false));
        }

        public ValueTask<IndexingGetVolumesResponse> GetVolumesAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingGetVolumesResponse(
                [new IndexingVolumeDescriptor(0x1234, @"C:\", "Test", 10, true, "Idle")]));

        public ValueTask<IndexingServiceStatusResponse> GetStatusAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingServiceStatusResponse(10, 1, 0, []));

        public ValueTask<IndexingVolumeOperationResponse> RebuildVolumeAsync(
            IndexingVolumeRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new IndexingVolumeOperationResponse(
                request.VolumeIdentity,
                request.RootPath,
                10,
                1,
                100,
                "Idle"));

        public ValueTask<IndexingVolumeOperationResponse> SyncVolumeAsync(
            IndexingVolumeRequest request,
            CancellationToken cancellationToken = default) =>
            RebuildVolumeAsync(request, cancellationToken);

        public async ValueTask<IndexingSearchResponse> SearchAsync(
            IndexingSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            if (SearchException is not null)
            {
                throw SearchException;
            }

            SearchStarted?.TrySetResult(true);
            if (SearchRelease is not null)
            {
                await SearchRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return SearchResponse ?? new IndexingSearchResponse([]);
        }

        public void Dispose()
        {
        }
    }
}
