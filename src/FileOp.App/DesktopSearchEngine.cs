using FileOp.Core.Indexing;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Windows.IndexingService;

namespace FileOp.App;

internal enum DesktopSearchMode
{
    Initializing,
    Native,
    Fallback,
    Unavailable,
}

internal sealed record DesktopSearchEngineState(
    DesktopSearchMode Mode,
    string Status,
    int IndexedItemCount,
    bool IsBusy,
    bool CanElevate,
    bool IsCurrent);

internal sealed class DesktopSearchEngine : IAsyncDisposable
{
    private const int FallbackBatchSize = 512;
    private const int InitialCatchUpBatchLimit = 64;
    private const int BackgroundCatchUpBatchLimit = 8;
    private const int CatchUpBusyRetryLimit = 8;
    private static readonly TimeSpan BackgroundSyncInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CatchUpBusyRetryDelay = TimeSpan.FromMilliseconds(75);

    private readonly InMemoryFileIndex _fallbackIndex = new();
    private readonly FileSystemCrawler _crawler = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _searchOperationGate = new(1, 1);
    private readonly SemaphoreSlim _nativeOperationGate = new(1, 1);
    private IndexingServiceProcessSession? _nativeSession;
    private IndexingVolumeDescriptor? _primaryVolume;
    private CancellationTokenSource? _syncCancellation;
    private Task? _syncLoop;
    private string? _fallbackRoot;
    private bool _fallbackReady;
    private bool _disposed;

    public DesktopSearchEngineState State { get; private set; } = new(
        DesktopSearchMode.Initializing,
        "Preparing search engine…",
        0,
        IsBusy: true,
        CanElevate: false,
        IsCurrent: false);

    public event Action<DesktopSearchEngineState>? StateChanged;

    public async Task InitializeAsync(string fallbackRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackRoot);
        ThrowIfDisposed();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var token = linkedCancellation.Token;

        await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _fallbackRoot = Path.GetFullPath(fallbackRoot);
            SetState(new DesktopSearchEngineState(
                DesktopSearchMode.Initializing,
                "Starting native NTFS index…",
                0,
                IsBusy: true,
                CanElevate: false,
                IsCurrent: false));

            var preparation = await TryStartNativeAsync(elevated: false, token).ConfigureAwait(false);
            if (preparation.IsNativeReady)
            {
                ApplyNativePreparation(preparation);
                if (!preparation.CanElevate)
                {
                    StartBackgroundSync();
                }

                return;
            }

            await BuildFallbackAsync(
                preparation.Status,
                preparation.CanElevate,
                token).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> TryElevateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(_fallbackRoot))
        {
            throw new InvalidOperationException("The search engine must be initialized before elevation is requested.");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var token = linkedCancellation.Token;

        await _lifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            SetState(State with
            {
                Status = "Requesting helper-only administrative indexing access…",
                IsBusy = true,
                CanElevate = false,
            });

            // Prevent an existing service search from reading the same per-volume SQLite
            // file while a replacement elevated helper may need to rebuild it.
            await _nativeOperationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var preparation = await TryStartNativeAsync(elevated: true, token).ConfigureAwait(false);
                if (!preparation.IsNativeReady)
                {
                    SetState(State with
                    {
                        Status = preparation.Status,
                        IsBusy = false,
                        CanElevate = preparation.CanElevate,
                    });
                    return false;
                }

                var previousSession = _nativeSession;
                ApplyNativePreparation(preparation);
                await StopBackgroundSyncAsync().ConfigureAwait(false);
                if (!preparation.CanElevate)
                {
                    StartBackgroundSync();
                }

                if (previousSession is not null && !ReferenceEquals(previousSession, _nativeSession))
                {
                    await previousSession.DisposeAsync().ConfigureAwait(false);
                }

                if (_fallbackReady)
                {
                    await _fallbackIndex.ClearAsync(token).ConfigureAwait(false);
                    _fallbackReady = false;
                }

                return true;
            }
            finally
            {
                _nativeOperationGate.Release();
            }
        }
        catch (OperationCanceledException) when (!_lifetimeCancellation.IsCancellationRequested)
        {
            SetState(State with
            {
                Status = "Administrative indexing access was not granted; the existing search mode remains active.",
                IsBusy = false,
                CanElevate = true,
            });
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException)
        {
            SetState(State with
            {
                Status = $"Could not start the elevated indexing helper: {exception.Message}",
                IsBusy = false,
                CanElevate = false,
            });
            return false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<FileRecord>> SearchAsync(string rawQuery, int limit = 250)
    {
        ThrowIfDisposed();
        await _searchOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var query = FileSearchQuery.Parse(rawQuery ?? string.Empty, limit);

            if (_nativeSession is { Client.IsConnected: true })
            {
                await _nativeOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                try
                {
                    if (_nativeSession is not { Client.IsConnected: true } session)
                    {
                        return await SearchFallbackAsync(query).ConfigureAwait(false);
                    }

                    return await SearchNativeAsync(session, query).ConfigureAwait(false);
                }
                finally
                {
                    _nativeOperationGate.Release();
                }
            }

            return await SearchFallbackAsync(query).ConfigureAwait(false);
        }
        finally
        {
            _searchOperationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        await StopBackgroundSyncAsync().ConfigureAwait(false);

        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _searchOperationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_nativeSession is not null)
                {
                    await _nativeSession.DisposeAsync().ConfigureAwait(false);
                    _nativeSession = null;
                }

                _primaryVolume = null;
                _fallbackIndex.Dispose();
            }
            finally
            {
                _searchOperationGate.Release();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        _searchOperationGate.Dispose();
        _nativeOperationGate.Dispose();
        _lifecycleGate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task<NativePreparationResult> TryStartNativeAsync(
        bool elevated,
        CancellationToken cancellationToken)
    {
        IndexingServiceProcessSession? candidate = null;
        try
        {
            var helperPath = IndexingServiceHelperLocator.ResolveAdjacentHelper(AppContext.BaseDirectory);
            candidate = await IndexingServiceProcessSession.StartAsync(
                helperPath,
                elevated,
                connectTimeout: TimeSpan.FromSeconds(20),
                cancellationToken).ConfigureAwait(false);

            var preparation = await PreparePrimaryVolumeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (!preparation.IsNativeReady)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                return preparation;
            }

            return preparation with { Session = candidate };
        }
        catch (OperationCanceledException)
        {
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (IndexingServiceRemoteException exception)
        {
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            return exception.Error.Code == IndexingServiceErrorCode.ElevationRequired
                ? NativePreparationResult.NeedsElevation(exception.Message)
                : NativePreparationResult.Failed($"Native indexing is unavailable: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or InvalidOperationException or TimeoutException or IOException)
        {
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }

            return NativePreparationResult.Failed($"Native indexing is unavailable: {exception.Message}");
        }
    }

    private async Task<NativePreparationResult> PreparePrimaryVolumeAsync(
        IndexingServiceProcessSession session,
        CancellationToken cancellationToken)
    {
        var root = Path.GetPathRoot(_fallbackRoot!);
        if (string.IsNullOrWhiteSpace(root))
        {
            return NativePreparationResult.Failed("Could not determine the Windows volume containing the user profile.");
        }

        var volumes = await session.Client.GetVolumesAsync(cancellationToken).ConfigureAwait(false);
        var volume = volumes.Volumes.FirstOrDefault(candidate =>
            string.Equals(
                NormalizeRoot(candidate.RootPath),
                NormalizeRoot(root),
                StringComparison.OrdinalIgnoreCase));

        if (volume is null)
        {
            return NativePreparationResult.Failed(
                $"{root} is not available through the NTFS indexing provider.");
        }

        var request = new IndexingVolumeRequest(volume.VolumeIdentity, volume.RootPath);
        if (!volume.HasCheckpoint)
        {
            SetState(State with
            {
                Status = $"Building fast NTFS index for {volume.RootPath}…",
                IndexedItemCount = volume.IndexedItemCount,
                IsBusy = true,
                CanElevate = false,
                IsCurrent = false,
            });

            try
            {
                var rebuilt = await session.Client.RebuildVolumeAsync(request, cancellationToken).ConfigureAwait(false);
                var catchUp = await CatchUpAsync(
                    session,
                    request,
                    rebuilt.NextUsn,
                    InitialCatchUpBatchLimit,
                    cancellationToken).ConfigureAwait(false);
                return NativePreparationResult.Ready(
                    session,
                    volume,
                    catchUp.IndexedItemCount,
                    catchUp.IsCurrent,
                    canElevate: false,
                    catchUp.IsCurrent
                        ? $"Fast NTFS index ready for {volume.RootPath}"
                        : $"Fast NTFS index ready for {volume.RootPath}; journal catch-up is continuing.");
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code == IndexingServiceErrorCode.ElevationRequired)
            {
                return NativePreparationResult.NeedsElevation(
                    $"Fast NTFS indexing for {volume.RootPath} needs administrator access. Using the profile fallback until access is granted.");
            }
        }

        SetState(State with
        {
            Status = $"Catching up NTFS changes for {volume.RootPath}…",
            IndexedItemCount = volume.IndexedItemCount,
            IsBusy = true,
            IsCurrent = false,
        });

        try
        {
            var catchUp = await CatchUpAsync(
                session,
                request,
                startingUsn: null,
                InitialCatchUpBatchLimit,
                cancellationToken).ConfigureAwait(false);
            return NativePreparationResult.Ready(
                session,
                volume,
                catchUp.IndexedItemCount,
                catchUp.IsCurrent,
                canElevate: false,
                catchUp.IsCurrent
                    ? $"Fast NTFS index ready for {volume.RootPath}"
                    : $"Fast NTFS index active for {volume.RootPath}; journal catch-up is continuing.");
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.ElevationRequired)
        {
            // The durable checkpoint is still valid if access was denied before a sync
            // operation could mutate or invalidate it. Keep the reviewed snapshot usable,
            // but make its lack of live catch-up explicit and offer helper-only elevation.
            return NativePreparationResult.Ready(
                session,
                volume,
                volume.IndexedItemCount,
                isCurrent: false,
                canElevate: true,
                $"Fast index available for {volume.RootPath}, but live NTFS updates need administrator access.");
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.SnapshotRequired)
        {
            SetState(State with
            {
                Status = $"Refreshing invalid NTFS snapshot for {volume.RootPath}…",
                IsBusy = true,
                IsCurrent = false,
            });

            try
            {
                var rebuilt = await session.Client.RebuildVolumeAsync(request, cancellationToken).ConfigureAwait(false);
                var catchUp = await CatchUpAsync(
                    session,
                    request,
                    rebuilt.NextUsn,
                    InitialCatchUpBatchLimit,
                    cancellationToken).ConfigureAwait(false);
                return NativePreparationResult.Ready(
                    session,
                    volume,
                    catchUp.IndexedItemCount,
                    catchUp.IsCurrent,
                    canElevate: false,
                    catchUp.IsCurrent
                        ? $"Fast NTFS index rebuilt for {volume.RootPath}"
                        : $"Fast NTFS index rebuilt for {volume.RootPath}; journal catch-up is continuing.");
            }
            catch (IndexingServiceRemoteException rebuildException)
                when (rebuildException.Error.Code == IndexingServiceErrorCode.ElevationRequired)
            {
                return NativePreparationResult.NeedsElevation(
                    $"The NTFS snapshot for {volume.RootPath} must be rebuilt with administrator access. Using the profile fallback until access is granted.");
            }
        }
    }

    private async Task<CatchUpResult> CatchUpAsync(
        IndexingServiceProcessSession session,
        IndexingVolumeRequest request,
        long? startingUsn,
        int batchLimit,
        CancellationToken cancellationToken)
    {
        var previousUsn = startingUsn;
        var indexedItemCount = 0;
        var busyRetries = 0;
        var batch = 0;

        while (batch < batchLimit)
        {
            IndexingVolumeOperationResponse response;
            try
            {
                response = await session.Client.SyncVolumeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code == IndexingServiceErrorCode.Busy &&
                      busyRetries < CatchUpBusyRetryLimit)
            {
                busyRetries++;
                await Task.Delay(CatchUpBusyRetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            busyRetries = 0;
            indexedItemCount = response.IndexedItemCount;
            if (previousUsn is { } previous && response.NextUsn == previous)
            {
                return new CatchUpResult(indexedItemCount, IsCurrent: true);
            }

            previousUsn = response.NextUsn;
            batch++;
        }

        return new CatchUpResult(indexedItemCount, IsCurrent: false);
    }

    private async Task BuildFallbackAsync(
        string nativeFailureStatus,
        bool canElevate,
        CancellationToken cancellationToken)
    {
        var root = _fallbackRoot!;
        SetState(new DesktopSearchEngineState(
            DesktopSearchMode.Initializing,
            $"{nativeFailureStatus} Indexing {root} with the safe fallback…",
            0,
            IsBusy: true,
            CanElevate: false,
            IsCurrent: false));

        await _fallbackIndex.ClearAsync(cancellationToken).ConfigureAwait(false);
        var batch = new List<FileRecord>(FallbackBatchSize);
        var indexed = 0;

        await foreach (var record in _crawler.CrawlAsync(root, cancellationToken).ConfigureAwait(false))
        {
            batch.Add(record);
            if (batch.Count < FallbackBatchSize)
            {
                continue;
            }

            await _fallbackIndex.AddBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            indexed += batch.Count;
            batch.Clear();

            if (indexed % 4096 == 0)
            {
                SetState(State with
                {
                    Status = $"Fallback indexing {root}…",
                    IndexedItemCount = indexed,
                });
            }
        }

        if (batch.Count > 0)
        {
            await _fallbackIndex.AddBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            indexed += batch.Count;
        }

        _fallbackReady = true;
        SetState(new DesktopSearchEngineState(
            DesktopSearchMode.Fallback,
            canElevate
                ? "Profile fallback snapshot ready. Helper-only administrator access can enable fast NTFS indexing."
                : "Profile fallback snapshot ready.",
            indexed,
            IsBusy: false,
            CanElevate: canElevate,
            IsCurrent: false));
    }

    private async ValueTask<IReadOnlyList<FileRecord>> SearchFallbackAsync(FileSearchQuery query)
    {
        if (!_fallbackReady)
        {
            return [];
        }

        return await _fallbackIndex.SearchAsync(query, _lifetimeCancellation.Token).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<FileRecord>> SearchNativeAsync(
        IndexingServiceProcessSession session,
        FileSearchQuery query)
    {
        var limit = query.Limit;
        while (true)
        {
            try
            {
                var response = await session.Client.SearchAsync(
                    new IndexingSearchRequest(query.Raw, limit),
                    _lifetimeCancellation.Token).ConfigureAwait(false);
                return response.Results.Select(ToFileRecord).ToArray();
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code == IndexingServiceErrorCode.ResponseTooLarge && limit > 16)
            {
                limit = Math.Max(16, limit / 2);
            }
        }
    }

    private void ApplyNativePreparation(NativePreparationResult preparation)
    {
        _nativeSession = preparation.Session
            ?? throw new InvalidOperationException("A native-ready preparation must own an indexing session.");
        _primaryVolume = preparation.Volume
            ?? throw new InvalidOperationException("A native-ready preparation must identify its primary volume.");

        SetState(new DesktopSearchEngineState(
            DesktopSearchMode.Native,
            preparation.Status,
            preparation.IndexedItemCount,
            IsBusy: false,
            CanElevate: preparation.CanElevate,
            IsCurrent: preparation.IsCurrent));
    }

    private void StartBackgroundSync()
    {
        _syncCancellation?.Cancel();
        _syncCancellation?.Dispose();
        _syncCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _syncLoop = Task.Run(() => BackgroundSyncAsync(_syncCancellation.Token));
    }

    private async Task StopBackgroundSyncAsync()
    {
        if (_syncCancellation is null)
        {
            return;
        }

        _syncCancellation.Cancel();
        if (_syncLoop is not null)
        {
            try
            {
                await _syncLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _syncCancellation.Dispose();
        _syncCancellation = null;
        _syncLoop = null;
    }

    private async Task BackgroundSyncAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(BackgroundSyncInterval, cancellationToken).ConfigureAwait(false);

            var session = _nativeSession;
            var volume = _primaryVolume;
            if (session is null || volume is null)
            {
                continue;
            }

            if (!session.Client.IsConnected)
            {
                var handled = await TransitionToFallbackFromBackgroundAsync(
                    session,
                    "Native indexing helper disconnected.",
                    canElevate: false,
                    cancellationToken).ConfigureAwait(false);
                if (handled)
                {
                    return;
                }

                continue;
            }

            if (!await _nativeOperationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var transitionToFallback = false;
            var fallbackCanElevate = false;
            var fallbackReason = string.Empty;

            try
            {
                var request = new IndexingVolumeRequest(volume.VolumeIdentity, volume.RootPath);
                var catchUp = await CatchUpAsync(
                    session,
                    request,
                    startingUsn: null,
                    BackgroundCatchUpBatchLimit,
                    cancellationToken).ConfigureAwait(false);

                SetState(State with
                {
                    Status = catchUp.IsCurrent
                        ? $"Fast NTFS index active for {volume.RootPath}"
                        : $"Fast NTFS index active for {volume.RootPath}; journal catch-up is continuing.",
                    IndexedItemCount = catchUp.IndexedItemCount,
                    IsBusy = false,
                    IsCurrent = catchUp.IsCurrent,
                    CanElevate = false,
                });
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code == IndexingServiceErrorCode.ElevationRequired)
            {
                SetState(State with
                {
                    Status = $"Fast index available for {volume.RootPath}, but live NTFS updates need administrator access.",
                    IsBusy = false,
                    CanElevate = true,
                    IsCurrent = false,
                });
                return;
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code == IndexingServiceErrorCode.Busy)
            {
                SetState(State with
                {
                    Status = $"Another FileOp session is updating {volume.RootPath}; native search will resume when that maintenance lease is free.",
                    IsBusy = true,
                    IsCurrent = false,
                });
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code == IndexingServiceErrorCode.SnapshotRequired)
            {
                SetState(State with
                {
                    Status = $"Refreshing invalid NTFS snapshot for {volume.RootPath}…",
                    IsBusy = true,
                    CanElevate = false,
                    IsCurrent = false,
                });

                try
                {
                    var request = new IndexingVolumeRequest(volume.VolumeIdentity, volume.RootPath);
                    var rebuilt = await session.Client.RebuildVolumeAsync(request, cancellationToken).ConfigureAwait(false);
                    var catchUp = await CatchUpAsync(
                        session,
                        request,
                        rebuilt.NextUsn,
                        BackgroundCatchUpBatchLimit,
                        cancellationToken).ConfigureAwait(false);

                    SetState(State with
                    {
                        Status = catchUp.IsCurrent
                            ? $"Fast NTFS index rebuilt for {volume.RootPath}"
                            : $"Fast NTFS index rebuilt for {volume.RootPath}; journal catch-up is continuing.",
                        IndexedItemCount = catchUp.IndexedItemCount,
                        IsBusy = false,
                        CanElevate = false,
                        IsCurrent = catchUp.IsCurrent,
                    });
                }
                catch (IndexingServiceRemoteException rebuildException)
                    when (rebuildException.Error.Code == IndexingServiceErrorCode.ElevationRequired)
                {
                    transitionToFallback = true;
                    fallbackCanElevate = true;
                    fallbackReason = $"The NTFS index for {volume.RootPath} needs an administrator-authorized rebuild.";
                }
                catch (IndexingServiceRemoteException rebuildException)
                    when (rebuildException.Error.Code == IndexingServiceErrorCode.Busy)
                {
                    transitionToFallback = true;
                    fallbackReason = $"Another FileOp session is rebuilding {volume.RootPath}.";
                }
                catch (IndexingServiceRemoteException rebuildException)
                {
                    transitionToFallback = true;
                    fallbackReason = $"The NTFS snapshot for {volume.RootPath} could not be rebuilt: {rebuildException.Message}";
                }
                catch (Exception rebuildException) when (rebuildException is IOException or InvalidOperationException)
                {
                    transitionToFallback = true;
                    fallbackReason = $"The NTFS snapshot for {volume.RootPath} could not be rebuilt: {rebuildException.Message}";
                }
            }
            catch (IndexingServiceRemoteException exception)
            {
                transitionToFallback = true;
                fallbackReason = $"Native index service failed for {volume.RootPath}: {exception.Message}";
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                transitionToFallback = true;
                fallbackReason = $"Native index connection stopped: {exception.Message}";
            }
            finally
            {
                _nativeOperationGate.Release();
            }

            if (transitionToFallback)
            {
                var handled = await TransitionToFallbackFromBackgroundAsync(
                    session,
                    fallbackReason,
                    fallbackCanElevate,
                    cancellationToken).ConfigureAwait(false);
                if (handled)
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> TransitionToFallbackFromBackgroundAsync(
        IndexingServiceProcessSession failedSession,
        string reason,
        bool canElevate,
        CancellationToken cancellationToken)
    {
        if (!await _lifecycleGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            if (!ReferenceEquals(_nativeSession, failedSession))
            {
                return true;
            }

            _nativeSession = null;
            _primaryVolume = null;
            await failedSession.DisposeAsync().ConfigureAwait(false);
            await BuildFallbackAsync(reason, canElevate, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void SetState(DesktopSearchEngineState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static FileRecord ToFileRecord(IndexingSearchResult result) => new(
        result.Path,
        result.Name,
        result.ParentPath,
        result.Extension,
        result.Length,
        result.IsDirectory,
        result.LastWriteTime,
        result.Attributes,
        result.Identity,
        result.ParentIdentity,
        result.AllocatedLength);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record CatchUpResult(int IndexedItemCount, bool IsCurrent);

    private sealed record NativePreparationResult(
        bool IsNativeReady,
        IndexingServiceProcessSession? Session,
        IndexingVolumeDescriptor? Volume,
        int IndexedItemCount,
        bool IsCurrent,
        bool CanElevate,
        string Status)
    {
        public static NativePreparationResult Ready(
            IndexingServiceProcessSession session,
            IndexingVolumeDescriptor volume,
            int indexedItemCount,
            bool isCurrent,
            bool canElevate,
            string status) =>
            new(true, session, volume, indexedItemCount, isCurrent, canElevate, status);

        public static NativePreparationResult NeedsElevation(string status) =>
            new(false, null, null, 0, false, true, status);

        public static NativePreparationResult Failed(string status) =>
            new(false, null, null, 0, false, false, status);
    }
}
