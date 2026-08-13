namespace FileOp.App;

public sealed partial class MainWindow
{
    private string? _lastStorageSourceIdentityKey;
    private bool _storageSourceIdentityTrackingInitialized;

    internal void InitializeStorageSourceIdentityTracking()
    {
        if (_storageSourceIdentityTrackingInitialized)
        {
            return;
        }

        _storageSourceIdentityTrackingInitialized = true;
        _lastStorageSourceIdentityKey = _searchEngine.StorageSourceIdentityKey;

        // MainWindow's existing source handlers use mode + root keys. Run this
        // identity boundary first so those handlers see a forced cache miss when
        // the physical native volume or rebuilt fallback snapshot changes while
        // retaining the same root path.
        _searchEngine.StateChanged -= SearchEngine_StateChanged;
        _searchEngine.StateChanged += StorageSourceIdentity_StateChanged;
        _searchEngine.StateChanged += SearchEngine_StateChanged;

        // XAML wires the Storage-aware elevation handler before this feature is
        // installed. Replace that root-only post-elevation comparison in production
        // with an equivalent handler that also binds the backing-source identity.
        EnableFastIndexButton.Click -= EnableFastIndexWithStorageTypesButton_Click;
        EnableFastIndexButton.Click += EnableFastIndexWithStorageSourceIdentityButton_Click;
        Closed += StorageSourceIdentityWindow_Closed;
    }

    private void StorageSourceIdentityWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        _searchEngine.StateChanged -= StorageSourceIdentity_StateChanged;
        EnableFastIndexButton.Click -= EnableFastIndexWithStorageSourceIdentityButton_Click;
        Closed -= StorageSourceIdentityWindow_Closed;
    }

    private void StorageSourceIdentity_StateChanged(DesktopSearchEngineState state)
    {
        if (_closed)
        {
            return;
        }

        // Native maintenance can replace the snapshot in place while preserving
        // both root and VolumeIdentity. Immediately invalidate only source-bound
        // Optimize work at busy entry; the engine advances the general source
        // generation on recovery, when every root-keyed feature can reload.
        if (state.Mode == DesktopSearchMode.Native && state.IsBusy)
        {
            Interlocked.Exchange(ref _storageOptimizationAnalysis, null);
            Interlocked.Exchange(ref _storageKnownLocationReview, null);
            Interlocked.Increment(ref _storageOptimizationGeneration);
        }

        var sourceIdentityKey = _searchEngine.StorageSourceIdentityKey;
        var previousSourceIdentityKey = Interlocked.Exchange(
            ref _lastStorageSourceIdentityKey,
            sourceIdentityKey);
        if (string.Equals(
                sourceIdentityKey,
                previousSourceIdentityKey,
                StringComparison.Ordinal))
        {
            return;
        }

        // These assignments are deliberately UI-free. StateChanged can arrive on
        // a background thread; the existing feature handlers perform collection
        // resets on the DispatcherQueue after observing the forced key miss.
        Interlocked.Exchange(ref _storageSourceKey, null);
        Interlocked.Exchange(ref _filesSourceKey, null);
        Interlocked.Exchange(ref _storageTypesSourceKey, null);
        Interlocked.Exchange(ref _storageHistorySourceKey, null);
        Interlocked.Exchange(ref _storageOptimizationSourceKey, null);

        // Same-size verification suppresses stale publication by comparing its
        // captured analysis object by reference rather than by generation. Clear
        // source-bound Optimize references with an interlocked exchange so a
        // verification that finishes before the queued UI source-reset handler
        // cannot publish old evidence. Disk I/O capture is intentionally system-
        // wide and therefore remains independent of this indexed-source transition.
        Interlocked.Exchange(ref _storageOptimizationAnalysis, null);
        Interlocked.Exchange(ref _storageKnownLocationReview, null);

        // Invalidate in-flight Storage work immediately as well. Individual
        // feature handlers may advance their generation again while refreshing;
        // that is harmless and keeps stale results from being published in the
        // interval before their UI-thread callbacks run.
        Interlocked.Increment(ref _storageGeneration);
        Interlocked.Increment(ref _storageTypeGeneration);
        Interlocked.Increment(ref _storageHistoryGeneration);
        Interlocked.Increment(ref _storageOptimizationGeneration);
    }

    private async void EnableFastIndexWithStorageSourceIdentityButton_Click(
        object sender,
        Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        var sourceBeforeElevation = _storageViewMode switch
        {
            StorageViewMode.Types => _storageTypesSourceKey,
            StorageViewMode.History => _storageHistorySourceKey,
            StorageViewMode.Optimize => _storageOptimizationSourceKey,
            _ => _storageSourceKey,
        };
        var sourceIdentityBeforeElevation = _searchEngine.StorageSourceIdentityKey;

        SearchBox.IsEnabled = false;
        StorageRefreshButton.IsEnabled = false;
        StorageTypesRefreshButton.IsEnabled = false;
        if (_storageHistoryInitialized)
        {
            _storageHistoryButton.IsEnabled = false;
            _storageHistoryView.SetReadyForRefresh(false);
        }
        if (_storageOptimizationInitialized)
        {
            _storageOptimizationButton.IsEnabled = false;
            _storageOptimizationView.SetReadyForRefresh(false);
        }
        EnableFastIndexButton.IsEnabled = false;
        SetSearchStatus(string.Empty);
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Increment(ref _storageGeneration);
        Interlocked.Increment(ref _storageTypeGeneration);
        Interlocked.Increment(ref _storageHistoryGeneration);
        Interlocked.Increment(ref _storageOptimizationGeneration);

        try
        {
            await _searchEngine.TryElevateAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_closed)
            {
                ApplyEngineState(_searchEngine.State);
            }
        }

        if (_closed)
        {
            return;
        }

        if (_activeSection == AppSection.Search && SearchBox.IsEnabled)
        {
            await RunSearchAsync();
            return;
        }

        if (_activeSection == AppSection.Storage &&
            !_searchEngine.State.IsBusy &&
            _searchEngine.StorageRootPath is { } root)
        {
            var currentSourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
            var currentSourceIdentityKey = _searchEngine.StorageSourceIdentityKey;
            if (string.Equals(
                    currentSourceKey,
                    sourceBeforeElevation,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    currentSourceIdentityKey,
                    sourceIdentityBeforeElevation,
                    StringComparison.Ordinal))
            {
                await LoadActiveStorageViewAsync(root, forceRefresh: true);
            }
        }
    }
}
