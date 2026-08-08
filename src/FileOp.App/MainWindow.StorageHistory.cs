using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MainWindow
{
    private const int StorageHistoryDisplayLimit = 90;

    private readonly StorageHistoryView _storageHistoryView = new();
    private readonly Button _storageHistoryButton = new()
    {
        Content = "History",
    };
    private IReadOnlyList<StorageHistorySnapshot> _storageHistorySnapshots = [];
    private string? _storageHistorySourceKey;
    private int _storageHistoryGeneration;
    private int _storageHistoryLoadingGeneration;
    private bool _storageHistoryInitialized;
    private bool _storageHistoryLoadedForSource;

    private void InitializeStorageHistoryView()
    {
        if (_storageHistoryInitialized)
        {
            return;
        }

        _storageHistoryInitialized = true;
        _storageHistoryButton.Click += StorageHistoryButton_Click;
        _storageHistoryView.RefreshRequested += StorageHistoryView_RefreshRequested;
        _storageHistoryView.Visibility = Visibility.Collapsed;

        if (StorageFoldersButton.Parent is StackPanel modePanel)
        {
            modePanel.Children.Add(_storageHistoryButton);
        }
        else
        {
            throw new InvalidOperationException("Storage mode buttons must be hosted by a StackPanel.");
        }

        if (StorageFolderPanel.Parent is Grid storageGrid)
        {
            Grid.SetRow(_storageHistoryView, 2);
            storageGrid.Children.Add(_storageHistoryView);
        }
        else
        {
            throw new InvalidOperationException("Storage content panels must be hosted by the Storage grid.");
        }

        _searchEngine.StateChanged += StorageHistoryEngine_StateChanged;
        _searchEngine.StorageHistoryCaptured += SearchEngine_StorageHistoryCaptured;
        Closed += StorageHistoryWindow_Closed;
        HandleStorageHistoryEngineState(_searchEngine.State);
    }

    private void StorageHistoryWindow_Closed(object sender, WindowEventArgs args)
    {
        _searchEngine.StateChanged -= StorageHistoryEngine_StateChanged;
        _searchEngine.StorageHistoryCaptured -= SearchEngine_StorageHistoryCaptured;
        _storageHistoryButton.Click -= StorageHistoryButton_Click;
        _storageHistoryView.RefreshRequested -= StorageHistoryView_RefreshRequested;
        Closed -= StorageHistoryWindow_Closed;
    }

    private void StorageHistoryEngine_StateChanged(DesktopSearchEngineState state)
    {
        if (_closed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            HandleStorageHistoryEngineState(state);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed)
            {
                HandleStorageHistoryEngineState(state);
            }
        });
    }

    private void SearchEngine_StorageHistoryCaptured(StorageHistorySnapshot snapshot)
    {
        if (_closed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed)
            {
                return;
            }

            _storageHistoryLoadedForSource = false;
            if (_storageViewMode == StorageViewMode.History && _storageHistoryLoadingGeneration == 0)
            {
                _ = LoadStorageHistoryAsync(forceRefresh: true);
            }
        });
    }

    private void HandleStorageHistoryEngineState(DesktopSearchEngineState state)
    {
        if (!_storageHistoryInitialized)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        var historyAvailable = _searchEngine.StorageHistoryAvailable;
        _storageHistoryButton.IsEnabled =
            _storageViewMode != StorageViewMode.History && historyAvailable;
        if (_storageHistoryLoadingGeneration == 0)
        {
            _storageHistoryView.SetReadyForRefresh(historyAvailable);
        }

        if (root is null)
        {
            _storageHistoryLoadedForSource = false;
            _storageHistoryLoadingGeneration = 0;
            if (_storageViewMode == StorageViewMode.History)
            {
                _storageHistoryView.SetUnavailable(
                    "Native history is unavailable until an indexed NTFS volume is active.");
            }
            return;
        }

        var sourceKey = CreateStorageSourceKey(state.Mode, root);
        var sourceChanged = !string.Equals(
            sourceKey,
            _storageHistorySourceKey,
            StringComparison.OrdinalIgnoreCase);
        if (sourceChanged)
        {
            _storageHistorySourceKey = sourceKey;
            _storageHistorySnapshots = [];
            _storageHistoryLoadedForSource = false;
            _storageHistoryLoadingGeneration = 0;
            Interlocked.Increment(ref _storageHistoryGeneration);
        }

        if (_storageViewMode != StorageViewMode.History)
        {
            return;
        }

        if (sourceChanged)
        {
            // The original MainWindow engine-state path is folder-only. Mark its source
            // cache as transitioned only when ownership actually changes so recurring
            // sync status updates cannot erase the visible History summary.
            _storageSourceKey = sourceKey;
            _storageAnalysis = null;
            _storageCurrentPath = null;
            _storageEntries.Clear();
            ResetStorageSummary();
            Interlocked.Increment(ref _storageGeneration);
        }

        if (!historyAvailable)
        {
            _storageHistoryView.SetUnavailable(
                state.Mode == DesktopSearchMode.Fallback
                    ? "Storage history is native-only. The profile fallback snapshot is not mixed into the native time series."
                    : "Native history is unavailable until the indexing helper is active.");
            SetStorageStatus("Storage history is currently unavailable for this indexing source.");
            return;
        }

        if ((sourceChanged || !_storageHistoryLoadedForSource) && _storageHistoryLoadingGeneration == 0)
        {
            _ = LoadStorageHistoryAsync(forceRefresh: true);
        }
    }

    private async void StorageHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _storageGeneration);
        Interlocked.Increment(ref _storageTypeGeneration);
        SetStorageViewMode(StorageViewMode.History);

        if (!_searchEngine.StorageHistoryAvailable)
        {
            _storageHistoryView.SetUnavailable(
                "Storage history is available only from the native NTFS index. Fallback snapshots are intentionally not mixed into this series.");
            SetStorageStatus("Native storage history is not currently available.");
            return;
        }

        await LoadStorageHistoryAsync(forceRefresh: false);
    }

    private async void StorageHistoryView_RefreshRequested(object? sender, EventArgs e)
    {
        await LoadStorageHistoryAsync(forceRefresh: true);
    }

    private async Task LoadStorageHistoryAsync(bool forceRefresh)
    {
        if (_closed)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null || !_searchEngine.StorageHistoryAvailable)
        {
            _storageHistoryLoadingGeneration = 0;
            _storageHistoryView.SetUnavailable(
                "Native storage history is not currently available.");
            SetStorageStatus("Native storage history is not currently available.");
            return;
        }

        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
        if (!string.Equals(sourceKey, _storageHistorySourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _storageHistorySourceKey = sourceKey;
            _storageHistorySnapshots = [];
            _storageHistoryLoadedForSource = false;
            _storageHistoryLoadingGeneration = 0;
            forceRefresh = true;
        }

        if (!forceRefresh && _storageHistoryLoadedForSource)
        {
            ApplyStorageHistory(_storageHistorySnapshots, root);
            return;
        }

        var generation = Interlocked.Increment(ref _storageHistoryGeneration);
        _storageHistoryLoadingGeneration = generation;
        _storageHistoryView.SetLoading("Loading native hourly observations from the shared index database…");
        SetStorageStatus("Loading storage history…");

        try
        {
            await _storageGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed || generation != Volatile.Read(ref _storageHistoryGeneration))
                {
                    return;
                }

                var snapshots = await _searchEngine.GetStorageHistoryAsync(StorageHistoryDisplayLimit);
                if (_closed || generation != Volatile.Read(ref _storageHistoryGeneration))
                {
                    return;
                }

                _storageHistorySnapshots = snapshots;
                _storageHistoryLoadedForSource = true;
                _storageHistoryLoadingGeneration = 0;
                ApplyStorageHistory(snapshots, root);
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
            if (!_closed && generation == Volatile.Read(ref _storageHistoryGeneration))
            {
                _storageHistoryLoadingGeneration = 0;
                _storageHistoryView.SetReadyForRefresh(true);
                SetStorageStatus("Storage history is temporarily busy. Refresh after the other index operation completes.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed && generation == Volatile.Read(ref _storageHistoryGeneration))
            {
                _storageHistoryLoadingGeneration = 0;
                _storageHistoryView.SetReadyForRefresh(_searchEngine.StorageHistoryAvailable);
                SetStorageStatus($"Storage history could not be loaded: {exception.Message}");
            }
        }
        finally
        {
            if (!_closed &&
                generation == Volatile.Read(ref _storageHistoryGeneration) &&
                _storageHistoryLoadingGeneration == generation)
            {
                _storageHistoryLoadingGeneration = 0;
                _storageHistoryView.SetReadyForRefresh(_searchEngine.StorageHistoryAvailable);
            }
        }
    }

    private void ApplyStorageHistory(IReadOnlyList<StorageHistorySnapshot> snapshots, string root)
    {
        _storageHistoryView.Apply(snapshots);
        StoragePathText.Text = root;

        var latest = snapshots
            .OrderByDescending(static snapshot => snapshot.CapturedAt)
            .FirstOrDefault();
        if (latest is null)
        {
            StorageLogicalText.Text = "—";
            StorageAllocatedText.Text = "—";
            StorageFilesText.Text = "—";
            StorageAliasesText.Text = "—";
            SetStorageStatus(
                "No native history observations yet. A low-priority capture will be attempted when the native index is current and foreground work is idle.");
            return;
        }

        StorageLogicalText.Text = ByteFormatter.Format(latest.LogicalBytes);
        StorageAllocatedText.Text = latest.AllocatedBytes is { } allocated
            ? ByteFormatter.Format(allocated)
            : "Unknown";
        StorageFilesText.Text = $"{latest.UniqueFileCount:N0}";
        StorageAliasesText.Text = $"{latest.HardLinkAliasCount:N0}";
        SetStorageStatus(
            $"{snapshots.Count:N0} native hourly observation(s) · latest bucket {latest.CapturedAt.ToLocalTime():g}");
    }
}
