using FileOp.Core.Indexing.Service;

namespace FileOp.Windows.IndexingService;

public interface IIndexingServiceBackend : IDisposable
{
    ValueTask<IndexingHelloResponse> HelloAsync(
        IndexingHelloRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IndexingGetVolumesResponse> GetVolumesAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IndexingServiceStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IndexingVolumeOperationResponse> RebuildVolumeAsync(
        IndexingVolumeRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IndexingVolumeOperationResponse> SyncVolumeAsync(
        IndexingVolumeRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IndexingSearchResponse> SearchAsync(
        IndexingSearchRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IndexingStorageAnalysisResponse> AnalyzeStorageAsync(
        IndexingStorageAnalysisRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IndexingStorageAnalysisResponse>(
            new IndexingServiceException(
                IndexingServiceErrorCode.InvalidRequest,
                "This indexing backend does not support storage analytics."));

    ValueTask<IndexingStorageFileTypeResponse> AnalyzeStorageTypesAsync(
        IndexingStorageFileTypeRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IndexingStorageFileTypeResponse>(
            new IndexingServiceException(
                IndexingServiceErrorCode.InvalidRequest,
                "This indexing backend does not support storage file-type analytics."));

    ValueTask<IndexingStorageHistoryCaptureResponse> CaptureStorageHistoryAsync(
        IndexingStorageHistoryCaptureRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IndexingStorageHistoryCaptureResponse>(
            new IndexingServiceException(
                IndexingServiceErrorCode.InvalidRequest,
                "This indexing backend does not support storage history capture."));

    ValueTask<IndexingStorageHistoryQueryResponse> GetStorageHistoryAsync(
        IndexingStorageHistoryQueryRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IndexingStorageHistoryQueryResponse>(
            new IndexingServiceException(
                IndexingServiceErrorCode.InvalidRequest,
                "This indexing backend does not support storage history queries."));
}