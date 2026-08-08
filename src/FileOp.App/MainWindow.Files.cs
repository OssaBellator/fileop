using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MainWindow
{
    private const int FilesDirectoryEntryLimit = 4_096;

    private FilesView _filesView = null!;
    private Button _filesNavigationButton = null!;
    private StorageDirectoryAnalysis? _filesAnalysis;
    private string? _filesCurrentPath;
    private string? _filesSourceKey;
    private string _lastFilesStatus = string.Empty;
    private int _filesGeneration;
    private bool _filesInitialized;
    private bool _filesVisible;

    internal void InitializeFilesFeature()
    {
        if (_filesInitialized)
        {
            return;
        }

        if (SearchNavigationButton.Parent is not StackPanel navigationPanel)
        {
            throw new InvalidOperationException("The primary navigation buttons must be hosted by a StackPanel.");
        }

        _filesNavigationButton = navigationPanel.Children
            .OfType<Button>()
            .FirstOrDefault(static button => string.Equals(button.Content as string, "Files", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The Files navigation placeholder could not be found.");

        if (SearchView.Parent is not Grid contentGrid)
        {
            throw new InvalidOperationException("The main content views must be hosted by a Grid.");
        }

        _filesView = new FilesView
        {
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_filesView, 0);
        contentGrid.Children.Add(_filesView);

        _filesInitialized = true;
        _filesNavigationButton.Click += FilesNavigationButton_Click;
        SearchNavigationButton.Click += FilesOtherNavigationButton_Click;
        StorageNavigationButton.Click += FilesOtherNavigationButton_Click;
        _filesView.UpRequested += FilesView_UpRequested;
        _filesView.RefreshRequested += FilesView_RefreshRequested;
        _filesView.EntryInvoked += FilesView_EntryInvoked;
        _searchEngine.StateChanged += FilesEngine_StateChanged;
        Closed += FilesWindow_Closed;

        HandleFilesEngineState(_searchEngine.State);
    }

    private void FilesWindow_Closed(object sender, WindowEventArgs args)
    {
        _searchEngine.StateChanged -= FilesEngine_StateChanged;
        _filesNavigationButton.Click -= FilesNavigationButton_Click;
        SearchNavigationButton.Click -= FilesOtherNavigationButton_Click;
        StorageNavigationButton.Click -= FilesOtherNavigationButton_Click;
        _filesView.UpRequested -= FilesView_UpRequested;
        _filesView.RefreshRequested -= FilesView_RefreshRequested;
        _filesView.EntryInvoked -= FilesView_EntryInvoked;
        Closed -= FilesWindow_Closed;
    }

    private void FilesEngine_StateChanged(DesktopSearchEngineState state)
    {
        if (_closed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            HandleFilesEngineState(state);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed)
            {
                HandleFilesEngineState(state);
            }
        });
    }

    private void HandleFilesEngineState(DesktopSearchEngineState state)
    {
        if (!_filesInitialized)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        var sourceAvailable = !_closed &&
            !state.IsBusy &&
            (state.Mode is DesktopSearchMode.Native or DesktopSearchMode.Fallback) &&
            root is not null;
        _filesNavigationButton.IsEnabled = sourceAvailable;
        UpdateFilesSourceDescription(state.Mode);

        if (root is null)
        {
            _filesSourceKey = null;
            _filesCurrentPath = null;
            _filesAnalysis = null;
            Interlocked.Increment(ref _filesGeneration);
            if (_filesVisible)
            {
                _filesView.SetUnavailable("Indexed Files browsing will be available after the search source is ready.");
                SetFilesStatus("Indexed Files browsing is currently unavailable.");
            }
            return;
        }

        var sourceKey = CreateStorageSourceKey(state.Mode, root);
        var sourceChanged = !string.Equals(sourceKey, _filesSourceKey, StringComparison.OrdinalIgnoreCase);
        if (sourceChanged)
        {
            _filesSourceKey = sourceKey;
            _filesCurrentPath = null;
            _filesAnalysis = null;
            Interlocked.Increment(ref _filesGeneration);
        }

        if (!_filesVisible)
        {
            return;
        }

        if (!sourceAvailable)
        {
            if (state.IsBusy)
            {
                _filesAnalysis = null;
                Interlocked.Increment(ref _filesGeneration);
            }

            _filesView.SetUnavailable("Indexed Files browsing is temporarily unavailable while the shared index is changing.");
            SetFilesStatus("Files will be available when indexing is ready.");
            return;
        }

        if (sourceChanged || _filesAnalysis is null || _filesCurrentPath is null)
        {
            var target = _filesCurrentPath;
            if (string.IsNullOrWhiteSpace(target) || !IsPathWithinRoot(target, root))
            {
                target = root;
            }

            _ = LoadFilesDirectoryAsync(target, forceRefresh: true);
            return;
        }

        _filesView.SetReady(CanNavigateFilesUp(root), canRefresh: true);
    }

    private async void FilesNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _filesVisible = true;
        _activeSection = AppSection.Search;
        _searchTimer.Stop();
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Increment(ref _storageGeneration);
        Interlocked.Increment(ref _storageTypeGeneration);
        Interlocked.Increment(ref _storageHistoryGeneration);
        SetStorageViewMode(StorageViewMode.Folders);

        SearchView.Visibility = Visibility.Collapsed;
        StorageView.Visibility = Visibility.Collapsed;
        _filesView.Visibility = Visibility.Visible;
        SearchStatusText.Text = _lastFilesStatus;

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            _filesView.SetUnavailable("Indexed Files browsing will be available when indexing is ready.");
            SetFilesStatus("Files will be available when indexing is ready.");
            return;
        }

        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
        if (!string.Equals(sourceKey, _filesSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _filesSourceKey = sourceKey;
            _filesAnalysis = null;
            _filesCurrentPath = null;
        }

        var path = _filesCurrentPath;
        if (string.IsNullOrWhiteSpace(path) || !IsPathWithinRoot(path, root))
        {
            path = root;
        }

        await LoadFilesDirectoryAsync(path, forceRefresh: false);
    }

    private void FilesOtherNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_filesVisible)
        {
            return;
        }

        _filesVisible = false;
        Interlocked.Increment(ref _filesGeneration);
        _filesView.Visibility = Visibility.Collapsed;
    }

    private async void FilesView_UpRequested(object? sender, EventArgs e)
    {
        var root = _searchEngine.StorageRootPath;
        var current = _filesCurrentPath;
        if (root is null || current is null || PathsEqual(root, current))
        {
            return;
        }

        var parent = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !IsPathWithinRoot(parent, root))
        {
            parent = root;
        }

        await LoadFilesDirectoryAsync(parent, forceRefresh: false);
    }

    private async void FilesView_RefreshRequested(object? sender, EventArgs e)
    {
        var root = _searchEngine.StorageRootPath;
        if (root is null)
        {
            _filesView.SetUnavailable("Indexed Files browsing is not currently available.");
            return;
        }

        var path = _filesCurrentPath;
        if (string.IsNullOrWhiteSpace(path) || !IsPathWithinRoot(path, root))
        {
            path = root;
        }

        await LoadFilesDirectoryAsync(path, forceRefresh: true);
    }

    private async void FilesView_EntryInvoked(object? sender, FileBrowserRow row)
    {
        if (row.IsDirectory)
        {
            await LoadFilesDirectoryAsync(row.Path, forceRefresh: false);
            return;
        }

        OpenPath(row.Path, SetFilesStatus);
    }

    private async Task LoadFilesDirectoryAsync(string directoryPath, bool forceRefresh)
    {
        if (_closed || !_filesVisible)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            _filesView.SetUnavailable("Indexed Files browsing is not currently available.");
            SetFilesStatus("Files will be available when indexing is ready.");
            return;
        }

        if (!IsPathWithinRoot(directoryPath, root))
        {
            directoryPath = root;
        }

        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
        if (!string.Equals(sourceKey, _filesSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _filesSourceKey = sourceKey;
            _filesAnalysis = null;
            _filesCurrentPath = null;
            forceRefresh = true;
        }

        if (!forceRefresh &&
            _filesAnalysis is { } cached &&
            PathsEqual(cached.RootPath, directoryPath))
        {
            ApplyFilesAnalysis(cached, root);
            return;
        }

        var generation = Interlocked.Increment(ref _filesGeneration);
        _filesView.SetLoading(directoryPath, "Loading direct entries from the shared index…");
        SetFilesStatus($"Loading {directoryPath} from the shared index…");

        try
        {
            await _storageGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed || !_filesVisible || generation != Volatile.Read(ref _filesGeneration))
                {
                    return;
                }

                var analysis = await _searchEngine.AnalyzeStorageAsync(
                    directoryPath,
                    FilesDirectoryEntryLimit);
                if (_closed || !_filesVisible || generation != Volatile.Read(ref _filesGeneration))
                {
                    return;
                }

                ApplyFilesAnalysis(analysis, root);
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
            if (!_closed && _filesVisible && generation == Volatile.Read(ref _filesGeneration))
            {
                SetFilesStatus("The shared index is busy. Refresh Files after the current maintenance operation completes.");
            }
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.SnapshotRequired)
        {
            if (!_closed && _filesVisible && generation == Volatile.Read(ref _filesGeneration))
            {
                SetFilesStatus("The indexed namespace is refreshing. Files will be available when the snapshot is current again.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed && _filesVisible && generation == Volatile.Read(ref _filesGeneration))
            {
                SetFilesStatus($"Files could not be loaded: {exception.Message}");
            }
        }
        finally
        {
            if (!_closed && _filesVisible && generation == Volatile.Read(ref _filesGeneration))
            {
                _filesView.SetReady(CanNavigateFilesUp(root), !_searchEngine.State.IsBusy);
            }
        }
    }

    private void ApplyFilesAnalysis(StorageDirectoryAnalysis analysis, string root)
    {
        _filesAnalysis = analysis;
        _filesCurrentPath = analysis.RootPath;
        var rows = analysis.Entries
            .OrderByDescending(static entry => entry.IsDirectory)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(FileBrowserRow.FromEntry)
            .ToArray();

        _filesView.Apply(
            rows,
            analysis.RootPath,
            analysis.DirectEntryCount,
            CanNavigateFilesUp(root),
            canRefresh: !_searchEngine.State.IsBusy);

        var source = _searchEngine.State.Mode == DesktopSearchMode.Native
            ? "whole-volume native index"
            : "profile fallback snapshot";
        var bounded = rows.Length == analysis.DirectEntryCount
            ? string.Empty
            : $" · bounded to {rows.Length:N0} returned entries";
        SetFilesStatus(
            $"{rows.Length:N0} of {analysis.DirectEntryCount:N0} direct entries · {source}{bounded}");
    }

    private void UpdateFilesSourceDescription(DesktopSearchMode mode)
    {
        _filesView.SetSourceDescription(mode switch
        {
            DesktopSearchMode.Native =>
                "Browse direct children from the same whole-volume NTFS metadata index used by Search and Storage. No directory rescan is performed.",
            DesktopSearchMode.Fallback =>
                "Browse the completed user-profile fallback snapshot. No second filesystem scan is performed, and entries outside that snapshot are intentionally unavailable.",
            _ =>
                "Indexed directory browsing becomes available when the shared search source is ready.",
        });
    }

    private bool CanNavigateFilesUp(string root) =>
        !_closed &&
        !_searchEngine.State.IsBusy &&
        _filesCurrentPath is not null &&
        !PathsEqual(root, _filesCurrentPath);

    private void SetFilesStatus(string status)
    {
        _lastFilesStatus = status;
        if (_filesVisible)
        {
            SearchStatusText.Text = status;
        }
    }
}
