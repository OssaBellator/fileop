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
        Closed += StorageSourceIdentityWindow_Closed;
    }

    private void StorageSourceIdentityWindow_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        _searchEngine.StateChanged -= StorageSourceIdentity_StateChanged;
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
        if (string.Equals(
                sourceIdentityKey,
                _lastStorageSourceIdentityKey,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastStorageSourceIdentityKey = sourceIdentityKey;

        // These assignments are deliberately UI-free. StateChanged can arrive on
        // a background thread; the existing feature handlers perform collection
        // resets on the DispatcherQueue after observing the forced key miss.
        _storageSourceKey = null;
        _filesSourceKey = null;
        _storageTypesSourceKey = null;
        _storageHistorySourceKey = null;
        _storageOptimizationSourceKey = null;

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
}
