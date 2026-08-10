using System.IO.Pipes;
using System.Text.Json;
using FileOp.Core.Indexing.Service;

namespace FileOp.Windows.IndexingService;

public sealed class IndexingServiceClient : IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private bool _connectionFaulted;
    private bool _disposed;

    public IndexingServiceClient(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
    }

    public bool IsConnected => !_disposed && !_connectionFaulted && _pipe.IsConnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfConnectionFaulted();
        return _pipe.ConnectAsync(cancellationToken);
    }

    public ValueTask<IndexingHelloResponse> HelloAsync(
        string clientName,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingHelloRequest, IndexingHelloResponse>(
            IndexingServiceOperation.Hello,
            new IndexingHelloRequest(clientName),
            cancellationToken);

    public ValueTask<IndexingGetVolumesResponse> GetVolumesAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<object, IndexingGetVolumesResponse>(
            IndexingServiceOperation.GetVolumes,
            new { },
            cancellationToken);

    public ValueTask<IndexingServiceStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<object, IndexingServiceStatusResponse>(
            IndexingServiceOperation.GetStatus,
            new { },
            cancellationToken);

    public ValueTask<IndexingVolumeOperationResponse> RebuildVolumeAsync(
        IndexingVolumeRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingVolumeRequest, IndexingVolumeOperationResponse>(
            IndexingServiceOperation.RebuildVolume,
            request,
            cancellationToken);

    public ValueTask<IndexingVolumeOperationResponse> SyncVolumeAsync(
        IndexingVolumeRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingVolumeRequest, IndexingVolumeOperationResponse>(
            IndexingServiceOperation.SyncVolume,
            request,
            cancellationToken);

    public ValueTask<IndexingSearchResponse> SearchAsync(
        IndexingSearchRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingSearchRequest, IndexingSearchResponse>(
            IndexingServiceOperation.Search,
            request,
            cancellationToken);

    public ValueTask<IndexingDirectoryBrowseResponse> BrowseDirectoryAsync(
        IndexingDirectoryBrowseRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingDirectoryBrowseRequest, IndexingDirectoryBrowseResponse>(
            IndexingServiceOperation.BrowseDirectory,
            request,
            cancellationToken);

    public ValueTask<IndexingStorageAnalysisResponse> AnalyzeStorageAsync(
        IndexingStorageAnalysisRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingStorageAnalysisRequest, IndexingStorageAnalysisResponse>(
            IndexingServiceOperation.AnalyzeStorage,
            request,
            cancellationToken);

    public ValueTask<IndexingStorageFileTypeResponse> AnalyzeStorageTypesAsync(
        IndexingStorageFileTypeRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingStorageFileTypeRequest, IndexingStorageFileTypeResponse>(
            IndexingServiceOperation.AnalyzeStorageTypes,
            request,
            cancellationToken);

    public ValueTask<IndexingStorageOptimizationResponse> AnalyzeStorageOptimizationAsync(
        IndexingStorageOptimizationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingStorageOptimizationRequest, IndexingStorageOptimizationResponse>(
            IndexingServiceOperation.AnalyzeStorageOptimization,
            request,
            cancellationToken);

    public ValueTask<IndexingStorageHistoryCaptureResponse> CaptureStorageHistoryAsync(
        IndexingStorageHistoryCaptureRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingStorageHistoryCaptureRequest, IndexingStorageHistoryCaptureResponse>(
            IndexingServiceOperation.CaptureStorageHistory,
            request,
            cancellationToken);

    public ValueTask<IndexingStorageHistoryQueryResponse> GetStorageHistoryAsync(
        IndexingStorageHistoryQueryRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexingStorageHistoryQueryRequest, IndexingStorageHistoryQueryResponse>(
            IndexingServiceOperation.GetStorageHistory,
            request,
            cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipe.Dispose();
        _requestGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _requestGate.Dispose();
    }

    private async ValueTask<TResponse> SendAsync<TRequest, TResponse>(
        IndexingServiceOperation operation,
        TRequest payload,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfConnectionFaulted();
        if (!_pipe.IsConnected)
        {
            throw new InvalidOperationException("The indexing service client is not connected.");
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfConnectionFaulted();
            if (!_pipe.IsConnected)
            {
                throw new InvalidOperationException("The indexing service client is not connected.");
            }

            var requestId = Guid.NewGuid();
            var request = new IndexingServiceRequest(
                IndexingServiceProtocol.CurrentVersion,
                requestId,
                operation,
                JsonSerializer.SerializeToElement(payload, SerializerOptions));
            await IndexingPipeTransport.WriteAsync(_pipe, request, cancellationToken).ConfigureAwait(false);
            var response = await IndexingPipeTransport.ReadAsync<IndexingServiceResponse>(_pipe, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new EndOfStreamException("The indexing service disconnected before returning a response.");

            if (response.RequestId != requestId)
            {
                throw new InvalidDataException(
                    $"Indexing service response {response.RequestId} does not match request {requestId}.");
            }

            if (response.ProtocolVersion != IndexingServiceProtocol.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Indexing service protocol changed to {response.ProtocolVersion} during the session.");
            }

            if (!response.Success)
            {
                throw new IndexingServiceRemoteException(
                    response.Error ?? new IndexingServiceError(
                        IndexingServiceErrorCode.InternalError,
                        "The indexing service returned an unsuccessful response without an error payload."));
            }

            return response.Payload.Deserialize<TResponse>(SerializerOptions)
                ?? throw new InvalidDataException(
                    $"The indexing service returned an empty {typeof(TResponse).Name} response.");
        }
        catch (OperationCanceledException)
        {
            FaultConnection();
            throw;
        }
        catch (IOException)
        {
            FaultConnection();
            throw;
        }
        catch (InvalidDataException)
        {
            FaultConnection();
            throw;
        }
        catch (JsonException)
        {
            FaultConnection();
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void FaultConnection()
    {
        _connectionFaulted = true;
        _pipe.Dispose();
    }

    private void ThrowIfConnectionFaulted()
    {
        if (_connectionFaulted)
        {
            throw new InvalidOperationException(
                "The indexing service connection is no longer reusable after an interrupted or invalid exchange.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
