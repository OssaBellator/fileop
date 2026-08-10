using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MainWindow
{
    private StorageOptimizationView _storageOptimizationView = null!;
    private Button _storageOptimizationButton = null!;
    private StorageOptimizationAnalysis? _storageOptimizationAnalysis;
    private string? _storageOptimizationSourceKey;
    private int _storageOptimizationGeneration;
    private bool _storageOptimizationInitialized;
    private bool _storageOptimizationLoadedForSource;

    private void InitializeStorageOptimizationView()
    {
        if (_storageOptimizationInitialized)
        {
            return;
        }

        _storageOptimizationView = new StorageOptimizationView
        {
            Visibility = Visibility.Collapsed,
        };
        _storageOptimizationButton = new Button
        {
            Content = "Optimize",
        };
        _storageOptimizationInitialized = true;
        _storageOptimizationButton.Click += StorageOptimizationButton_Click;
        _storageOptimizationView.RefreshRequested += StorageOptimizationView_RefreshRequested;
        _storageOptimizationView.PerformanceRefreshRequested +=
            StorageOptimizationView_PerformanceRefreshRequested;

        if (StorageFoldersButton.Parent is StackPanel modePanel)
        {
            modePanel.Children.Add(_storageOptimizationButton);
        }
        else
        {
            throw new InvalidOperationException("Storage mode buttons must be hosted by a StackPanel.");
        }

        if (StorageFolderPanel.Parent is Grid storageGrid)
        {
            Grid.SetRow(_storageOptimizationView, 2);
            storageGrid.Children.Add(_storageOptimizationView);
        }
        else
        {
            throw new InvalidOperationException("Storage content panels must be hosted by the Storage grid.");
        }

        _searchEngine.StateChanged += StorageOptimizationEngine_StateChanged;
        Closed += StorageOptimizationWindow_Closed;
        HandleStorageOptimizationEngineState(_searchEngine.State);
    }

    private void StorageOptimizationWindow_Closed(object sender, WindowEventArgs args)
    {
        _searchEngine.StateChanged -= StorageOptimizationEngine_StateChanged;
        _storageOptimizationButton.Click -= StorageOptimizationButton_Click;
        _storageOptimizationView.RefreshRequested -= StorageOptimizationView_RefreshRequested;
        _storageOptimizationView.PerformanceRefreshRequested -=
            StorageOptimizationView_PerformanceRefreshRequested;
        Closed -= StorageOptimizationWindow_Closed;
    }

    private void StorageOptimizationEngine_StateChanged(DesktopSearchEngineState state)
    {
        if (_closed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            HandleStorageOptimizationEngineState(state);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed)
            {
                HandleStorageOptimizationEngineState(state);
            }
        });
    }

    private void HandleStorageOptimizationEngineState(DesktopSearchEngineState state)
    {
        if (!_storageOptimizationInitialized)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        var available = _searchEngine.StorageOptimizationAvailable && !state.IsBusy;
        _storageOptimizationButton.IsEnabled =
            _storageViewMode != StorageViewMode.Optimize && available;
        _storageOptimizationView.SetReadyForRefresh(
            _storageViewMode == StorageViewMode.Optimize && available);

        if (root is null)
        {
            _storageOptimizationLoadedForSource = false;
            if (_storageViewMode == StorageViewMode.Optimize)
            {
                _storageOptimizationView.SetUnavailable(
                    "Optimization analysis is unavailable until a native indexed NTFS volume is active.");
            }
            return;
        }

        var sourceKey = CreateStorageSourceKey(state.Mode, root);
        var sourceChanged = !string.Equals(
            sourceKey,
            _storageOptimizationSourceKey,
            StringComparison.OrdinalIgnoreCase);
        if (sourceChanged)
        {
            _storageOptimizationSourceKey = sourceKey;
            _storageOptimizationAnalysis = null;
            _storageOptimizationLoadedForSource = false;
            Interlocked.Increment(ref _storageOptimizationGeneration);
        }

        if (_storageViewMode != StorageViewMode.Optimize)
        {
            return;
        }

        if (sourceChanged)
        {
            _storageSourceKey = sourceKey;
            _storageAnalysis = null;
            _storageCurrentPath = null;
            _storageEntries.Clear();
            ResetStorageSummary();
            Interlocked.Increment(ref _storageGeneration);
        }

        if (!available)
        {
            _storageOptimizationView.SetUnavailable(
                state.Mode == DesktopSearchMode.Fallback
                    ? "Optimization recommendations currently require the native NTFS index; fallback snapshots are not presented as complete reclaim analysis."
                    : "Optimization analysis is unavailable while the native index is busy or disconnected.");
            SetStorageStatus("Storage optimization is currently unavailable for this indexing source.");
            return;
        }

        if (sourceChanged || !_storageOptimizationLoadedForSource)
        {
            _ = LoadStorageOptimizationAsync(forceRefresh: true);
        }
    }

    private async void StorageOptimizationButton_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _storageGeneration);
        Interlocked.Increment(ref _storageTypeGeneration);
        Interlocked.Increment(ref _storageHistoryGeneration);
        SetStorageViewMode(StorageViewMode.Optimize);

        if (!_searchEngine.StorageOptimizationAvailable)
        {
            _storageOptimizationView.SetUnavailable(
                "Optimization recommendations currently require the native NTFS index.");
            SetStorageStatus("Native storage optimization is not currently available.");
            return;
        }

        await LoadStorageOptimizationAsync(forceRefresh: false);
    }

    private async void StorageOptimizationView_RefreshRequested(object? sender, EventArgs e)
    {
        await LoadStorageOptimizationAsync(forceRefresh: true);
    }

    private async void StorageOptimizationView_PerformanceRefreshRequested(
        object? sender,
        EventArgs e)
    {
        if (_closed ||
            _storageViewMode != StorageViewMode.Optimize ||
            !_searchEngine.StorageOptimizationAvailable ||
            _searchEngine.State.IsBusy)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _storageOptimizationGeneration);
        _storageOptimizationView.SetPerformanceLoading();
        await CapturePerformanceDiagnosticsAsync(generation);
        if (!_closed && generation == Volatile.Read(ref _storageOptimizationGeneration))
        {
            _storageOptimizationView.SetReadyForRefresh(
                _searchEngine.StorageOptimizationAvailable && !_searchEngine.State.IsBusy);
        }
    }

    private async Task LoadStorageOptimizationAsync(bool forceRefresh)
    {
        if (_closed)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null || !_searchEngine.StorageOptimizationAvailable)
        {
            _storageOptimizationView.SetUnavailable(
                "Native storage optimization is not currently available.");
            SetStorageStatus("Native storage optimization is not currently available.");
            return;
        }

        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
        if (!string.Equals(sourceKey, _storageOptimizationSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _storageOptimizationSourceKey = sourceKey;
            _storageOptimizationAnalysis = null;
            _storageOptimizationLoadedForSource = false;
            forceRefresh = true;
        }

        var generation = Interlocked.Increment(ref _storageOptimizationGeneration);
        _storageOptimizationView.SetLoading(
            forceRefresh || _storageOptimizationAnalysis is null
                ? "Analyzing reclaim candidates and bounded performance probes from the current source…"
                : "Refreshing bounded performance probes from the current source…");
        SetStorageStatus("Refreshing storage and performance optimization evidence…");

        try
        {
            await _storageGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed || generation != Volatile.Read(ref _storageOptimizationGeneration))
                {
                    return;
                }

                var analysis = _storageOptimizationAnalysis;
                if (forceRefresh || analysis is null)
                {
                    analysis = await _searchEngine.AnalyzeStorageOptimizationAsync(root);
                    if (_closed || generation != Volatile.Read(ref _storageOptimizationGeneration))
                    {
                        return;
                    }

                    _storageOptimizationAnalysis = analysis;
                    _storageOptimizationLoadedForSource = true;
                }

                ApplyStorageOptimization(analysis);
                await CapturePerformanceDiagnosticsAsync(generation);
            }
            finally
            {
                _storageGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.Busy)
        {
            if (!_closed && generation == Volatile.Read(ref _storageOptimizationGeneration))
            {
                _storageOptimizationView.SetReadyForRefresh(true);
                SetStorageStatus("Storage optimization is temporarily busy. Refresh after index maintenance completes.");
            }
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.SnapshotRequired)
        {
            if (!_closed && generation == Volatile.Read(ref _storageOptimizationGeneration))
            {
                _storageOptimizationView.SetUnavailable(
                    "The native snapshot must be refreshed before optimization analysis can continue.");
                SetStorageStatus("Storage optimization is waiting for a fresh native index snapshot.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed && generation == Volatile.Read(ref _storageOptimizationGeneration))
            {
                _storageOptimizationView.SetReadyForRefresh(_searchEngine.StorageOptimizationAvailable);
                SetStorageStatus($"Storage optimization failed: {exception.Message}");
            }
        }
        finally
        {
            if (!_closed && generation == Volatile.Read(ref _storageOptimizationGeneration))
            {
                _storageOptimizationView.SetReadyForRefresh(
                    _storageViewMode == StorageViewMode.Optimize &&
                    _searchEngine.StorageOptimizationAvailable &&
                    !_searchEngine.State.IsBusy);
            }
        }
    }

    private async Task CapturePerformanceDiagnosticsAsync(int generation)
    {
        try
        {
            var diagnostics = await _searchEngine.CapturePerformanceDiagnosticsAsync();
            if (_closed || generation != Volatile.Read(ref _storageOptimizationGeneration))
            {
                return;
            }

            _storageOptimizationView.ApplyPerformanceDiagnostics(diagnostics);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed && generation == Volatile.Read(ref _storageOptimizationGeneration))
            {
                _storageOptimizationView.SetPerformanceUnavailable(
                    $"Performance diagnostics are unavailable: {exception.Message}");
            }
        }
    }

    private void ApplyStorageOptimization(StorageOptimizationAnalysis analysis)
    {
        _storageOptimizationView.Apply(analysis);
        StoragePathText.Text = analysis.RootPath;
        StorageLogicalText.Text = "—";
        StorageAllocatedText.Text = "—";
        StorageFilesText.Text = "—";
        StorageAliasesText.Text = "—";
        SetStorageStatus(
            $"Read-only optimization advisor · {analysis.LargestFiles.Count:N0} large file(s) · " +
            $"{analysis.StaleLargeFiles.Count:N0} old large file(s) · " +
            $"{analysis.SameSizeCandidateGroups.Count:N0} same-size group(s)");
    }
}
