namespace FileOp.App;

public sealed partial class MainWindow
{
    private readonly object _searchSourceIdentityGate = new();
    private string? _lastSearchSourceIdentityKey;
    private int _lastSearchSourceOwnedGeneration;
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
            // includes Native maintenance and helper replacement, but also elevation
            // that begins while the current mode is Fallback. Invalidate an in-flight
            // query immediately; unchanged backing tokens deliberately keep already-
            // displayed rows intact when a busy operation ultimately changes nothing.
            if (state.IsBusy)
            {
                InvalidateSearchFromSource();
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

            // An actual token change also invalidates already-displayed Search
            // evidence. Later source-owned busy invalidations must not cancel this
            // pending clear; only a later user-owned Search generation may do so.
            InvalidateSearchFromSource();
            QueueSearchPresentationInvalidation(sourceIdentityKey);
        }
    }

    private void InvalidateSearchFromSource()
    {
        // StateChanged callbacks are not globally serialized. The dedicated source
        // lock above keeps source-owned increments ordered while user Search work may
        // still increment _searchGeneration concurrently with Interlocked operations.
        _lastSearchSourceOwnedGeneration = Interlocked.Increment(ref _searchGeneration);
    }

    private void QueueSearchPresentationInvalidation(string? sourceIdentityKey)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            _ = ApplySearchPresentationInvalidationAsync(sourceIdentityKey);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            _ = ApplySearchPresentationInvalidationAsync(sourceIdentityKey);
        });
    }

    private async Task ApplySearchPresentationInvalidationAsync(string? sourceIdentityKey)
    {
        if (_closed)
        {
            return;
        }

        try
        {
            // Drain any request that already owns the Search gate. Its outer error
            // handler runs on the UI context after releasing the gate. Releasing
            // immediately here and yielding one UI turn ensures that stale status
            // publication finishes before this source-change message is applied.
            // A fresh query may increment _searchGeneration during the same interval.
            await _searchGate.WaitAsync(_lifetimeCancellation.Token);
            _searchGate.Release();
            await Task.Yield();

            lock (_searchSourceIdentityGate)
            {
                var currentGeneration = Volatile.Read(ref _searchGeneration);
                if (_closed ||
                    currentGeneration != _lastSearchSourceOwnedGeneration ||
                    !string.Equals(
                        sourceIdentityKey,
                        _searchEngine.StorageSourceIdentityKey,
                        StringComparison.Ordinal))
                {
                    return;
                }

                // If currentGeneration is still the latest source-owned generation,
                // no newer user Search request is eligible to publish. A later busy
                // source event may have advanced it, which intentionally keeps this
                // clear valid because that event also invalidated any intervening query.
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
