namespace FileOp.App;

public sealed partial class MainWindow
{
    private readonly object _searchSourceIdentityGate = new();
    private string? _lastSearchSourceIdentityKey;
    private long _searchSourceChangeSequence;
    private bool _searchSourceIdentityTrackingInitialized;

    internal void InitializeSearchSourceIdentityTracking()
    {
        if (_searchSourceIdentityTrackingInitialized)
        {
            return;
        }

        _searchSourceIdentityTrackingInitialized = true;
        _lastSearchSourceIdentityKey = _searchEngine.StorageSourceIdentityKey;

        // Storage source identity tracking is installed first and updates the
        // shared backing-source token. Run this Search publication boundary next,
        // before the normal UI state handler is queued/applied.
        _searchEngine.StateChanged -= SearchEngine_StateChanged;
        _searchEngine.StateChanged += SearchSourceIdentity_StateChanged;
        _searchEngine.StateChanged += SearchEngine_StateChanged;
        Closed += SearchSourceIdentityWindow_Closed;
    }

    private void SearchSourceIdentityWindow_Closed(
        object sender,
        Microsoft.UI.Xaml.WindowEventArgs args)
    {
        _searchEngine.StateChanged -= SearchSourceIdentity_StateChanged;
        Closed -= SearchSourceIdentityWindow_Closed;
    }

    private void SearchSourceIdentity_StateChanged(DesktopSearchEngineState state)
    {
        lock (_searchSourceIdentityGate)
        {
            if (_closed)
            {
                return;
            }

            // Any busy publication can precede a backing-source transition. This
            // includes Native maintenance/helper replacement and elevation that
            // begins while the current mode is Fallback. Invalidate any in-flight
            // query immediately; unchanged backing tokens deliberately keep the
            // already-displayed presentation when the operation changes nothing.
            if (state.IsBusy)
            {
                Interlocked.Increment(ref _searchGeneration);
            }

            var sourceIdentityKey = _searchEngine.StorageSourceIdentityKey;
            var previousSourceIdentityKey = Interlocked.Exchange(
                ref _lastSearchSourceIdentityKey,
                sourceIdentityKey);
            if (string.Equals(
                    sourceIdentityKey,
                    previousSourceIdentityKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            // A real backing-token change owns one final presentation clear. Do
            // not let unrelated request-generation increments (text debounce,
            // elevation invalidation, or a racing query) cancel that stale-source
            // boundary. A newer source change advances this independent sequence
            // and supersedes the older queued clear even if a token later cycles
            // back to the same string value.
            Interlocked.Increment(ref _searchGeneration);
            var sourceChangeSequence = Interlocked.Increment(
                ref _searchSourceChangeSequence);
            QueueSearchPresentationInvalidation(
                sourceChangeSequence,
                sourceIdentityKey);
        }
    }

    private void QueueSearchPresentationInvalidation(
        long sourceChangeSequence,
        string? sourceIdentityKey)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            _ = ApplySearchPresentationInvalidationAsync(
                sourceChangeSequence,
                sourceIdentityKey);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            _ = ApplySearchPresentationInvalidationAsync(
                sourceChangeSequence,
                sourceIdentityKey);
        });
    }

    private async Task ApplySearchPresentationInvalidationAsync(
        long sourceChangeSequence,
        string? sourceIdentityKey)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            // Drain any request that already owns the Search gate. Its result or
            // outer error handler may finish on the UI context after the source
            // transition began. Releasing immediately and yielding one UI turn
            // makes this source-owned clear the final presentation for that source
            // change. A query racing the transition may therefore need to be run
            // once more afterwards; preserving possibly stale rows is not preferred.
            await _searchGate.WaitAsync(_lifetimeCancellation.Token);
            _searchGate.Release();
            await Task.Yield();

            lock (_searchSourceIdentityGate)
            {
                if (_closed ||
                    sourceChangeSequence != Volatile.Read(
                        ref _searchSourceChangeSequence) ||
                    !string.Equals(
                        sourceIdentityKey,
                        _searchEngine.StorageSourceIdentityKey,
                        StringComparison.Ordinal))
                {
                    return;
                }

                _results.Clear();
                SetSearchStatus(sourceIdentityKey is null
                    ? "Search source is changing. Search will be available when indexing is ready."
                    : "Search source changed. Search again to show results from the current index.");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_closed)
        {
        }
    }
}
