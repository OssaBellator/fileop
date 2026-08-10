using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.Ntfs;
using Microsoft.Data.Sqlite;

namespace FileOp.Windows.IndexingService;

public sealed class StorageHistoryIndexingServiceBackend : IIndexingServiceBackend
{
    private readonly string _databaseDirectory;
    private readonly NtfsIndexingServiceBackend _inner;
    private readonly Func<DateTimeOffset> _utcNow;
    private bool _disposed;

    public StorageHistoryIndexingServiceBackend(
        string databaseDirectory,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
        _databaseDirectory = Path.GetFullPath(databaseDirectory);
        Directory.CreateDirectory(_databaseDirectory);
        _inner = new NtfsIndexingServiceBackend(_databaseDirectory);
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public ValueTask<IndexingHelloResponse> HelloAsync(
        IndexingHelloRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.HelloAsync(request, cancellationToken);

    public ValueTask<IndexingGetVolumesResponse> GetVolumesAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetVolumesAsync(cancellationToken);

    public ValueTask<IndexingServiceStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetStatusAsync(cancellationToken);

    public ValueTask<IndexingVolumeOperationResponse> RebuildVolumeAsync(
        IndexingVolumeRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.RebuildVolumeAsync(request, cancellationToken);

    public ValueTask<IndexingVolumeOperationResponse> SyncVolumeAsync(
        IndexingVolumeRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.SyncVolumeAsync(request, cancellationToken);

    public ValueTask<IndexingSearchResponse> SearchAsync(
        IndexingSearchRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.SearchAsync(request, cancellationToken);

    public ValueTask<IndexingStorageAnalysisResponse> AnalyzeStorageAsync(
        IndexingStorageAnalysisRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.AnalyzeStorageAsync(request, cancellationToken);

    public ValueTask<IndexingStorageFileTypeResponse> AnalyzeStorageTypesAsync(
        IndexingStorageFileTypeRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.AnalyzeStorageTypesAsync(request, cancellationToken);

    public async ValueTask<IndexingStorageHistoryCaptureResponse> CaptureStorageHistoryAsync(
        IndexingStorageHistoryCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        // AnalyzeStorageTypes is the reviewed semantic read boundary: it resolves the
        // attached volume/root, validates the directory, takes the per-volume operation
        // gate and cross-process read lease, verifies the durable checkpoint and fully
        // materializes exact category totals before returning.
        var live = await _inner.AnalyzeStorageTypesAsync(
            new IndexingStorageFileTypeRequest(
                request.VolumeIdentity,
                request.VolumeRootPath,
                request.DirectoryPath,
                MaxTypes: 1),
            cancellationToken).ConfigureAwait(false);

        var capturedAt = StorageHistoryCapturePolicy.GetHourlyBucket(_utcNow());
        long snapshotId;
        try
        {
            using var history = new SqliteStorageHistoryStore(CreateDatabasePath(
                request.VolumeIdentity,
                request.VolumeRootPath));
            snapshotId = await history.SaveSnapshotAsync(
                live.Analysis,
                capturedAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsDatabaseBusy(exception))
        {
            throw CreateHistoryBusyException(request.DirectoryPath, exception);
        }

        var snapshot = new StorageHistorySnapshot(
            snapshotId,
            live.Analysis.RootPath,
            capturedAt,
            live.Analysis.LogicalBytes,
            live.Analysis.AllocatedBytes,
            live.Analysis.FileCount,
            live.Analysis.HardLinkAliasCount,
            live.Analysis.TypeCount,
            live.Analysis.Categories.Select(static category =>
                new StorageHistoryCategorySnapshot(
                    category.Category,
                    category.LogicalBytes,
                    category.AllocatedBytes,
                    category.FileCount,
                    category.HardLinkAliasCount,
                    category.TypeCount)).ToArray());
        return new IndexingStorageHistoryCaptureResponse(snapshot);
    }

    public async ValueTask<IndexingStorageHistoryQueryResponse> GetStorageHistoryAsync(
        IndexingStorageHistoryQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        ValidateAttachedVolume(request.VolumeIdentity, request.VolumeRootPath);
        ValidateDirectoryWithinVolume(request.DirectoryPath, request.VolumeRootPath);

        try
        {
            // Historical observations are independently persisted and validated. They remain
            // queryable even when the current namespace later loses its durable checkpoint.
            using var history = new SqliteStorageHistoryStore(CreateDatabasePath(
                request.VolumeIdentity,
                request.VolumeRootPath));
            var snapshots = await history.GetSnapshotsAsync(
                request.DirectoryPath,
                request.Limit,
                cancellationToken).ConfigureAwait(false);
            return new IndexingStorageHistoryQueryResponse(snapshots);
        }
        catch (SqliteException exception) when (IsDatabaseBusy(exception))
        {
            throw CreateHistoryBusyException(request.DirectoryPath, exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
    }

    private void ValidateAttachedVolume(ulong volumeIdentity, string volumeRootPath)
    {
        var requestedRoot = NormalizeRoot(volumeRootPath);
        var exists = NtfsVolumeDiscovery.GetVolumes().Any(candidate =>
            candidate.VolumeIdentity == volumeIdentity &&
            string.Equals(
                NormalizeRoot(candidate.RootPath),
                requestedRoot,
                StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.VolumeNotFound,
                $"NTFS volume {volumeRootPath} ({volumeIdentity:X16}) is not currently available.",
                canRetry: true);
        }
    }

    private static void ValidateDirectoryWithinVolume(string directoryPath, string volumeRootPath)
    {
        var directory = NormalizeRoot(directoryPath);
        var volume = NormalizeRoot(volumeRootPath);
        if (!directory.StartsWith(volume, StringComparison.OrdinalIgnoreCase))
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.InvalidRequest,
                $"{directoryPath} is outside the requested volume root {volumeRootPath}.");
        }
    }

    private string CreateDatabasePath(ulong volumeIdentity, string volumeRootPath) =>
        IndexDatabasePathResolver.CreatePath(_databaseDirectory, volumeIdentity, volumeRootPath);

    private static bool IsDatabaseBusy(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6;

    private static IndexingServiceException CreateHistoryBusyException(
        string directoryPath,
        SqliteException exception) =>
        new(
            IndexingServiceErrorCode.Busy,
            $"Storage history for {directoryPath} is temporarily busy. Retry after the current database operation completes.",
            canRetry: true,
            exception);

    private static string NormalizeRoot(string rootPath) =>
        Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
