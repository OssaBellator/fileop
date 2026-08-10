using System.ComponentModel;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Performance;
using FileOp.Core.Search;
using FileOp.Core.Storage;
using FileOp.Windows.Ntfs;
using Microsoft.Data.Sqlite;

namespace FileOp.Windows.IndexingService;

public sealed class StorageOptimizationIndexingServiceBackend : IIndexingServiceBackend
{
    private readonly string _databaseDirectory;
    private readonly PagedDirectoryIndexingServiceBackend _inner;
    private readonly Func<DateTimeOffset> _utcNow;
    private bool _disposed;

    public StorageOptimizationIndexingServiceBackend(
        string databaseDirectory,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
        _databaseDirectory = Path.GetFullPath(databaseDirectory);
        Directory.CreateDirectory(_databaseDirectory);
        _inner = new PagedDirectoryIndexingServiceBackend(_databaseDirectory);
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

    public async ValueTask<IndexingIndexDiagnosticsResponse> GetIndexDiagnosticsAsync(
        IndexingIndexDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var volume = ResolveVolume(request.VolumeIdentity, request.VolumeRootPath);
        var databasePath = CreateDatabasePath(volume.VolumeIdentity, volume.RootPath);
        var processGate = new IndexingVolumeFileGate(databasePath);
        using var processLease = processGate.TryAcquireRead();
        if (processLease is null)
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.Busy,
                $"{volume.RootPath} index diagnostics are unavailable while another FileOp indexing process is maintaining it.",
                canRetry: true);
        }

        try
        {
            var browser = new SqliteFileDirectoryBrowser(databasePath);
            var sourceKey = NtfsIndexSynchronizer.CreateSourceKey(volume);
            if (!await browser.HasCheckpointAsync(sourceKey, cancellationToken).ConfigureAwait(false))
            {
                throw new IndexingServiceException(
                    IndexingServiceErrorCode.SnapshotRequired,
                    $"{volume.RootPath} has no valid durable NTFS checkpoint for index diagnostics.",
                    canRetry: true);
            }

            var reader = new SqliteIndexDatabaseDiagnosticsReader(databasePath);
            var diagnostics = await reader
                .ReadWithCheckpointAsync(sourceKey, _utcNow(), cancellationToken)
                .ConfigureAwait(false);

            if (diagnostics.DurableCheckpoint is { } checkpoint)
            {
                try
                {
                    var journal = new NtfsUsnJournal().Query(volume);
                    diagnostics = diagnostics with
                    {
                        JournalFreshness = new IndexJournalFreshnessDiagnostics(
                            checkpoint.JournalId,
                            checkpoint.NextUsn,
                            checkpoint.UpdatedAt,
                            journal.JournalId,
                            journal.LowestValidUsn,
                            journal.NextUsn),
                    };
                }
                catch (Win32Exception)
                {
                    // Durable checkpoint/database evidence remains valid when the live
                    // journal metadata cannot be opened in the current security context.
                }
                catch (UnauthorizedAccessException)
                {
                    // Treat access failure as optional live-evidence loss, not as a
                    // failure of the already-completed read-only SQLite diagnostics.
                }
            }

            return new IndexingIndexDiagnosticsResponse(diagnostics);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.Busy,
                $"Index diagnostics for {volume.RootPath} are temporarily busy.",
                canRetry: true,
                exception);
        }
    }

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

    public ValueTask<IndexingDirectoryBrowseResponse> BrowseDirectoryAsync(
        IndexingDirectoryBrowseRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.BrowseDirectoryAsync(request, cancellationToken);

    public ValueTask<IndexingStorageAnalysisResponse> AnalyzeStorageAsync(
        IndexingStorageAnalysisRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.AnalyzeStorageAsync(request, cancellationToken);

    public ValueTask<IndexingStorageFileTypeResponse> AnalyzeStorageTypesAsync(
        IndexingStorageFileTypeRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.AnalyzeStorageTypesAsync(request, cancellationToken);

    public async ValueTask<IndexingStorageOptimizationResponse> AnalyzeStorageOptimizationAsync(
        IndexingStorageOptimizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var volume = ResolveVolume(request.VolumeIdentity, request.VolumeRootPath);
        ValidateDirectoryWithinVolume(request.DirectoryPath, volume.RootPath);
        var databasePath = CreateDatabasePath(volume.VolumeIdentity, volume.RootPath);
        var processGate = new IndexingVolumeFileGate(databasePath);
        using var processLease = processGate.TryAcquireRead();
        if (processLease is null)
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.Busy,
                $"{volume.RootPath} cannot be optimized while another FileOp indexing process is maintaining it.",
                canRetry: true);
        }

        try
        {
            var browser = new SqliteFileDirectoryBrowser(databasePath);
            var sourceKey = NtfsIndexSynchronizer.CreateSourceKey(volume);
            if (!await browser.HasCheckpointAsync(sourceKey, cancellationToken).ConfigureAwait(false))
            {
                throw new IndexingServiceException(
                    IndexingServiceErrorCode.SnapshotRequired,
                    $"{volume.RootPath} has no valid durable NTFS checkpoint for storage optimization.",
                    canRetry: true);
            }

            if (!await browser.DirectoryExistsAsync(request.DirectoryPath, cancellationToken).ConfigureAwait(false))
            {
                throw new IndexingServiceException(
                    IndexingServiceErrorCode.InvalidRequest,
                    $"{request.DirectoryPath} is not an indexed directory on {volume.RootPath}.");
            }

            var analytics = new SqliteStorageOptimizationAnalytics(databasePath);
            var analysis = await analytics.AnalyzeOptimizationAsync(
                request.DirectoryPath,
                StorageOptimizationPolicy.Default,
                _utcNow(),
                cancellationToken).ConfigureAwait(false);
            return new IndexingStorageOptimizationResponse(analysis);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.Busy,
                $"Storage optimization for {request.DirectoryPath} is temporarily busy.",
                canRetry: true,
                exception);
        }
    }

    public ValueTask<IndexingStorageHistoryCaptureResponse> CaptureStorageHistoryAsync(
        IndexingStorageHistoryCaptureRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.CaptureStorageHistoryAsync(request, cancellationToken);

    public ValueTask<IndexingStorageHistoryQueryResponse> GetStorageHistoryAsync(
        IndexingStorageHistoryQueryRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.GetStorageHistoryAsync(request, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
    }

    private static NtfsVolume ResolveVolume(ulong volumeIdentity, string volumeRootPath)
    {
        var requestedRoot = NormalizeRoot(volumeRootPath);
        var volume = NtfsVolumeDiscovery.GetVolumes().FirstOrDefault(candidate =>
            candidate.VolumeIdentity == volumeIdentity &&
            string.Equals(
                NormalizeRoot(candidate.RootPath),
                requestedRoot,
                StringComparison.OrdinalIgnoreCase));
        return volume ?? throw new IndexingServiceException(
            IndexingServiceErrorCode.VolumeNotFound,
            $"NTFS volume {volumeRootPath} ({volumeIdentity:X16}) is not currently available.",
            canRetry: true);
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

    private static string NormalizeRoot(string rootPath) =>
        Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
