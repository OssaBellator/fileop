namespace FileOp.App;

public sealed partial class MainWindow
{
    private string? _lastSearchSourceIdentityKey;
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
        if (_closed)
        {
            return;
        }

        // A Native busy publication can precede an in-place snapshot rebuild or
        // helper replacement. Invalidate an in-flight query immediately, even
        // though a failed elevation deliberately keeps the backing token stable.
        if (state.Mode == DesktopSearchMode.Native && state.IsBusy)
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

        // A completed source-token change invalidates both in-flight and already
        // displayed Search evidence. The queued clear owns this exact generation:
        // if the user starts a fresh query first, RunSearchAsync advances the
        // generation and the stale clear cannot erase those new results.
        var invalidationGeneration = Interlocked.Increment(ref _searchGeneration);
        QueueSearchPresentationInvalidation(invalidationGeneration, sourceIdentityKey);
    }

    private void QueueSearchPresentationInvalidation(
        int invalidationGeneration,
        string? sourceIdentityKey)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            _ = ApplySearchPresentationInvalidationAsync(
                invalidationGeneration,
                sourceIdentityKey);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            _ = ApplySearchPresentationInvalidationAsync(
                invalidationGeneration,
                sourceIdentityKey);
        });
    }

    private async Task ApplySearchPresentationInvalidationAsync(
        int invalidationGeneration,
        string? sourceIdentityKey)
    {
        try
        {
            // RunSearchAsync releases this gate before its outer error handler can
            // complete. Waiting here ensures an invalidated old request cannot
            // overwrite the final source-changed status with a stale failure. A
            // fresh query may acquire the gate first; its newer generation then
            // makes this invalidation self-cancel after that query completes.
            await _searchGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed ||
                    invalidationGeneration != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }

                _results.Clear();
                SetSearchStatus(sourceIdentityKey is null
                    ? "Search source is changing. Search will be available when indexing is ready."
                    : "Search source changed. Search again to show results from the current index.");
            }
            finally
            {
                _searchGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }
}
