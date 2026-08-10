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
    private int _performanceDiskIoGeneration;
    private bool _performanceDiskIoCaptureActive;
    private bool _storageSameSizeVerificationActive;
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
        _storageOptimizationView.PerformanceDiskIoCaptureRequested +=
            StorageOptimizationView_PerformanceDiskIoCaptureRequested;
        _storageOptimizationView.SameSizeVerificationRequested +=
            StorageOptimizationView_SameSizeVerificationRequested;

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
        Interlocked.Increment(ref _performanceDiskIoGeneration);
        _performanceDiskIoCaptureActive = false;
        _storageSameSizeVerificationActive = false;
        _searchEngine.StateChanged -= StorageOptimizationEngine_StateChanged;
        _storageOptimizationButton.Click -= StorageOptimizationButton_Click;
        _storageOptimizationView.RefreshRequested -= StorageOptimizationView_RefreshRequested;
        _storageOptimizationView.PerformanceRefreshRequested -=
            StorageOptimizationView_PerformanceRefreshRequested;
        _storageOptimizationView.PerformanceDiskIoCaptureRequested -=
            StorageOptimizationView_PerformanceDiskIoCaptureRequested;
        _storageOptimizationView.SameSizeVerificationRequested -=
            StorageOptimizationView_SameSizeVerificationRequested;
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
        var storageAvailable = _searchEngine.StorageOptimizationAvailable && !state.IsBusy;
        _storageOptimizationButton.IsEnabled = _storageViewMode != StorageViewMode.Optimize;
        _storageOptimizationView.SetReadyForRefresh(
            _storageViewMode == StorageViewMode.Optimize &&
            storageAvailable &&
            !_storageSameSizeVerificationActive);
        _storageOptimizationView.SetDiskIoReadyForCapture(
            _storageViewMode == StorageViewMode.Optimize &&
            !_performanceDiskIoCaptureActive &&
            !_storageSameSizeVerificationActive);

        if (root is null)
        {
            _storageOptimizationAnalysis = null;
            _storageOptimizationLoadedForSource = false;
            if (_storageViewMode == StorageViewMode.Optimize)
            {
                _storageOptimizationView.SetUnavailable(
                    "Optimization analysis is unavailable until a native indexed NTFS volume is active. Disk I/O attribution remains available as a separate system-wide capture.");
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

        if (_storageSameSizeVerificationActive && !sourceChanged)
        {
            SetStorageStatus(
                "Content verification is running; index availability changes will be reevaluated after it completes.");
            return;
        }

        if (!storageAvailable)
        {
            _storageOptimizationView.SetUnavailable(
                state.Mode == DesktopSearchMode.Fallback
                    ? "Optimization recommendations currently require the native NTFS index; fallback snapshots are not presented as complete reclaim analysis. Disk I/O attribution remains independently available."
                    : "Optimization analysis is unavailable while the native index is busy or disconnected. Disk I/O attribution remains independently available.");
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
        _storageOptimizationView.SetDiskIoReadyForCapture(
            !_performanceDiskIoCaptureActive && !_storageSameSizeVerificationActive);

        if (!_searchEngine.StorageOptimizationAvailable || _searchEngine.State.IsBusy)
        {
            _storageOptimizationView.SetUnavailable(
                "Native reclaim analysis is not currently available. Disk I/O attribution remains available as a separate system-wide capture.");
            SetStorageStatus("Native storage optimization is not currently available.");
            return;
        }

        await LoadStorageOptimizationAsync(forceRefresh: false);
    }

    private async void StorageOptimizationView_RefreshRequested(object? sender, EventArgs e)
    {
        if (_storageSameSizeVerificationActive)
        {
            return;
        }

        await LoadStorageOptimizationAsync(forceRefresh: true);
    }

    private async void StorageOptimizationView_PerformanceRefreshRequested(
        object? sender,
        EventArgs e)
    {
        if (_closed ||
            _storageViewMode != StorageViewMode.Optimize ||
            _storageSameSizeVerificationActive ||
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
                _searchEngine.StorageOptimizationAvailable &&
                !_searchEngine.State.IsBusy &&
                !_storageSameSizeVerificationActive);
        }
    }

    private async void StorageOptimizationView_PerformanceDiskIoCaptureRequested(
        object? sender,
        EventArgs e)
    {
        if (_closed ||
            _storageViewMode != StorageViewMode.Optimize ||
            _storageSameSizeVerificationActive ||
            _performanceDiskIoCaptureActive)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _performanceDiskIoGeneration);
        _performanceDiskIoCaptureActive = true;
        _storageOptimizationView.SetDiskIoLoading();
        try
        {
            var result = await _searchEngine.CaptureDiskIoAttributionAsync();
            if (_closed || generation != Volatile.Read(ref _performanceDiskIoGeneration))
            {
                return;
            }

            _storageOptimizationView.ApplyDiskIoCapture(result);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed && generation == Volatile.Read(ref _performanceDiskIoGeneration))
            {
                _storageOptimizationView.SetDiskIoUnavailable(
                    $"Disk I/O attribution capture failed: {exception.Message}");
            }
        }
        finally
        {
            _performanceDiskIoCaptureActive = false;
            if (!_closed && generation == Volatile.Read(ref _performanceDiskIoGeneration))
            {
                _storageOptimizationView.SetDiskIoReadyForCapture(
                    _storageViewMode == StorageViewMode.Optimize &&
                    !_storageSameSizeVerificationActive);
            }
        }
    }

    private async void StorageOptimizationView_SameSizeVerificationRequested(
        object? sender,
        StorageSameSizeVerificationRequestedEventArgs e)
    {
        if (_closed ||
            _storageViewMode != StorageViewMode.Optimize ||
            _storageSameSizeVerificationActive ||
            _performanceDiskIoCaptureActive ||
            _storageOptimizationAnalysis is not { } analysis ||
            e.GroupIndex < 0 ||
            e.GroupIndex >= analysis.SameSizeCandidateGroups.Count)
        {
            return;
        }

        var group = analysis.SameSizeCandidateGroups[e.GroupIndex];
        _storageSameSizeVerificationActive = true;
        _storageOptimizationView.SetSameSizeVerificationLoading(e.GroupIndex);
        _storageOptimizationView.SetReadyForRefresh(false);
        _storageOptimizationView.SetDiskIoReadyForCapture(false);
        try
        {
            var verification = await _searchEngine.VerifySameSizeContentAsync(
                analysis.RootPath,
                group);
            if (_closed || !ReferenceEquals(analysis, _storageOptimizationAnalysis))
            {
                return;
            }

            _storageOptimizationView.ApplySameSizeVerification(
                e.GroupIndex,
                verification);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed && ReferenceEquals(analysis, _storageOptimizationAnalysis))
            {
                _storageOptimizationView.SetSameSizeVerificationFailure(
                    e.GroupIndex,
                    $"Content verification failed: {exception.Message}");
            }
        }
        finally
        {
            _storageSameSizeVerificationActive = false;
            if (!_closed)
            {
                if (ReferenceEquals(analysis, _storageOptimizationAnalysis))
                {
                    _storageOptimizationView.SetReadyForRefresh(
                        _storageViewMode == StorageViewMode.Optimize &&
                        _searchEngine.StorageOptimizationAvailable &&
                        !_searchEngine.State.IsBusy);
                    _storageOptimizationView.SetDiskIoReadyForCapture(
                        _storageViewMode == StorageViewMode.Optimize &&
                        !_performanceDiskIoCaptureActive);
                }
                else
                {
                    HandleStorageOptimizationEngineState(_searchEngine.State);
                }
            }
        }
    }

    private async Task LoadStorageOptimizationAsync(bool forceRefresh)
    {
        if (_closed || _storageSameSizeVerificationActive)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null || !_searchEngine.StorageOptimizationAvailable)
        {
            _storageOptimizationView.SetUnavailable(
                "Native storage optimization is not currently available. Disk I/O attribution remains independently available.");
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
                    "The native snapshot must be refreshed before optimization analysis can continue. Disk I/O attribution remains independently available.");
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
                    !_searchEngine.State.IsBusy &&
                    !_storageSameSizeVerificationActive);
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
