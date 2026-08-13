using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;

namespace FileOp.App;

internal enum StorageHistoryCaptureAttemptKind
{
    Captured,
    NotDue,
    Busy,
    Unavailable,
    Failed,
}

internal sealed record StorageHistoryCaptureAttempt(
    StorageHistoryCaptureAttemptKind Kind,
    StorageHistorySnapshot? Snapshot = null,
    string? Message = null);

internal sealed partial class DesktopSearchEngine
{
    private const int MaximumStorageHistoryLimit = 4_096;
    private static readonly TimeSpan HistoryPostSyncYieldDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HistoryForegroundBusyRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HistoryServiceBusyRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HistoryUnavailableRetryDelay = TimeSpan.FromMinutes(15);

    private long _lastSuccessfulHistoryBucketUtcTicks = long.MinValue;
    private long _nextHistoryCaptureAttemptUtcTicks;
    private int _historyCaptureInProgress;

    public DesktopSearchEngine()
    {
        StateChanged += TrackStorageSourceIdentity;
        StateChanged += StorageHistoryCapture_StateChanged;
    }

    public bool StorageHistoryAvailable =>
        State.Mode == DesktopSearchMode.Native &&
        _primaryVolume is not null &&
        _nativeSession is { Client.IsConnected: true };

    public event Action<StorageHistorySnapshot>? StorageHistoryCaptured;

    public async ValueTask<StorageHistoryCaptureAttempt> TryCaptureStorageHistoryAsync()
    {
        ThrowIfDisposed();
        if (State.Mode != DesktopSearchMode.Native ||
            !State.IsCurrent ||
            _primaryVolume is null ||
            _nativeSession is not { Client.IsConnected: true })
        {
            return new StorageHistoryCaptureAttempt(StorageHistoryCaptureAttemptKind.Unavailable);
        }

        if (Interlocked.CompareExchange(ref _historyCaptureInProgress, 1, 0) != 0)
        {
            return new StorageHistoryCaptureAttempt(StorageHistoryCaptureAttemptKind.NotDue);
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var bucket = StorageHistoryCapturePolicy.GetHourlyBucket(now);
            var bucketTicks = bucket.UtcDateTime.Ticks;
            if (Volatile.Read(ref _lastSuccessfulHistoryBucketUtcTicks) == bucketTicks ||
                now.UtcDateTime.Ticks < Volatile.Read(ref _nextHistoryCaptureAttemptUtcTicks))
            {
                return new StorageHistoryCaptureAttempt(StorageHistoryCaptureAttemptKind.NotDue);
            }

            if (!await _searchOperationGate.WaitAsync(0, _lifetimeCancellation.Token).ConfigureAwait(false))
            {
                DeferHistoryCapture(now, HistoryForegroundBusyRetryDelay);
                return new StorageHistoryCaptureAttempt(StorageHistoryCaptureAttemptKind.Busy);
            }

            try
            {
                if (!await _nativeOperationGate.WaitAsync(0, _lifetimeCancellation.Token).ConfigureAwait(false))
                {
                    DeferHistoryCapture(now, HistoryForegroundBusyRetryDelay);
                    return new StorageHistoryCaptureAttempt(StorageHistoryCaptureAttemptKind.Busy);
                }

                try
                {
                    if (State.Mode != DesktopSearchMode.Native ||
                        !State.IsCurrent ||
                        _primaryVolume is not { } volume ||
                        _nativeSession is not { Client.IsConnected: true } session)
                    {
                        DeferHistoryCapture(now, HistoryUnavailableRetryDelay);
                        return new StorageHistoryCaptureAttempt(StorageHistoryCaptureAttemptKind.Unavailable);
                    }

                    var response = await session.Client.CaptureStorageHistoryAsync(
                        new IndexingStorageHistoryCaptureRequest(
                            volume.VolumeIdentity,
                            volume.RootPath,
                            volume.RootPath),
                        _lifetimeCancellation.Token).ConfigureAwait(false);
                    Volatile.Write(
                        ref _lastSuccessfulHistoryBucketUtcTicks,
                        response.Snapshot.CapturedAt.ToUniversalTime().UtcDateTime.Ticks);
                    Volatile.Write(ref _nextHistoryCaptureAttemptUtcTicks, 0);
                    return new StorageHistoryCaptureAttempt(
                        StorageHistoryCaptureAttemptKind.Captured,
                        response.Snapshot);
                }
                finally
                {
                    _nativeOperationGate.Release();
                }
            }
            finally
            {
                _searchOperationGate.Release();
            }
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.Busy)
        {
            DeferHistoryCapture(DateTimeOffset.UtcNow, HistoryServiceBusyRetryDelay);
            return new StorageHistoryCaptureAttempt(
                StorageHistoryCaptureAttemptKind.Busy,
                Message: exception.Message);
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code is IndexingServiceErrorCode.SnapshotRequired or
                IndexingServiceErrorCode.ElevationRequired or
                IndexingServiceErrorCode.VolumeNotFound)
        {
            DeferHistoryCapture(DateTimeOffset.UtcNow, HistoryUnavailableRetryDelay);
            return new StorageHistoryCaptureAttempt(
                StorageHistoryCaptureAttemptKind.Unavailable,
                Message: exception.Message);
        }
        catch (Exception exception) when (exception is IndexingServiceRemoteException or IOException or InvalidOperationException)
        {
            DeferHistoryCapture(DateTimeOffset.UtcNow, HistoryUnavailableRetryDelay);
            return new StorageHistoryCaptureAttempt(
                StorageHistoryCaptureAttemptKind.Failed,
                Message: exception.Message);
        }
        finally
        {
            Volatile.Write(ref _historyCaptureInProgress, 0);
        }
    }

    public async ValueTask<IReadOnlyList<StorageHistorySnapshot>> GetStorageHistoryAsync(int limit = 90)
    {
        ThrowIfDisposed();
        if (limit <= 0 || limit > MaximumStorageHistoryLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"Storage history limits must be between 1 and {MaximumStorageHistoryLimit:N0}.");
        }

        await _searchOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _nativeOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            try
            {
                if (State.Mode != DesktopSearchMode.Native ||
                    _primaryVolume is not { } volume ||
                    _nativeSession is not { Client.IsConnected: true } session)
                {
                    throw new InvalidOperationException(
                        "Storage history is available only while the native NTFS index session is active.");
                }

                var requestedLimit = limit;
                while (true)
                {
                    try
                    {
                        var response = await session.Client.GetStorageHistoryAsync(
                            new IndexingStorageHistoryQueryRequest(
                                volume.VolumeIdentity,
                                volume.RootPath,
                                volume.RootPath,
                                requestedLimit),
                            _lifetimeCancellation.Token).ConfigureAwait(false);
                        return response.Snapshots;
                    }
                    catch (IndexingServiceRemoteException exception)
                        when (exception.Error.Code == IndexingServiceErrorCode.ResponseTooLarge && requestedLimit > 8)
                    {
                        requestedLimit = Math.Max(8, requestedLimit / 2);
                    }
                }
            }
            finally
            {
                _nativeOperationGate.Release();
            }
        }
        finally
        {
            _searchOperationGate.Release();
        }
    }

    private void StorageHistoryCapture_StateChanged(DesktopSearchEngineState state)
    {
        if (_disposed ||
            state.Mode != DesktopSearchMode.Native ||
            !state.IsCurrent ||
            state.IsBusy)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var bucketTicks = StorageHistoryCapturePolicy.GetHourlyBucket(now).UtcDateTime.Ticks;
        if (Volatile.Read(ref _lastSuccessfulHistoryBucketUtcTicks) == bucketTicks ||
            now.UtcDateTime.Ticks < Volatile.Read(ref _nextHistoryCaptureAttemptUtcTicks))
        {
            return;
        }

        _ = RunScheduledStorageHistoryCaptureAsync();
    }

    private async Task RunScheduledStorageHistoryCaptureAsync()
    {
        try
        {
            // Background synchronization raises StateChanged before it releases the
            // native operation gate. Yield beyond that callback, then make one
            // non-blocking attempt so foreground Search/Storage always wins.
            await Task.Delay(HistoryPostSyncYieldDelay, _lifetimeCancellation.Token).ConfigureAwait(false);
            var attempt = await TryCaptureStorageHistoryAsync().ConfigureAwait(false);
            if (attempt.Kind == StorageHistoryCaptureAttemptKind.Captured && attempt.Snapshot is not null)
            {
                StorageHistoryCaptured?.Invoke(attempt.Snapshot);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private void DeferHistoryCapture(DateTimeOffset now, TimeSpan delay)
    {
        var retryAt = now.ToUniversalTime().Add(delay).UtcDateTime.Ticks;
        Volatile.Write(ref _nextHistoryCaptureAttemptUtcTicks, retryAt);
    }
}
