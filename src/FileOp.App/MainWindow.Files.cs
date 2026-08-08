using FileOp.Core.Indexing.Service;
using FileOp.Core.Models;
using FileOp.Windows.IndexingService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MainWindow
{
    private const int FilesPageSize = 256;

    private readonly List<FileBrowserRow> _filesRows = [];
    private FilesView _filesView = null!;
    private Button _filesNavigationButton = null!;
    private FileDirectoryBrowseCursor? _filesNextCursor;
    private string? _filesCurrentPath;
    private string? _filesSourceKey;
    private string _lastFilesStatus = string.Empty;
    private int _filesCurrentTotalCount;
    private int _filesGeneration;
    private int _filesLoadingGeneration;
    private bool _filesInitialized;
    private bool _filesLoadedForSource;
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
        _filesView.LoadMoreRequested += FilesView_LoadMoreRequested;
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
        _filesView.LoadMoreRequested -= FilesView_LoadMoreRequested;
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
            ResetFilesPaging(clearPath: true);
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
            ResetFilesPaging(clearPath: true);
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
                ResetFilesPaging(clearPath: false);
                Interlocked.Increment(ref _filesGeneration);
            }

            _filesView.SetUnavailable("Indexed Files browsing is temporarily unavailable while the shared index is changing.");
            SetFilesStatus("Files will be available when indexing is ready.");
            return;
        }

        if ((sourceChanged || !_filesLoadedForSource || _filesCurrentPath is null) &&
            _filesLoadingGeneration == 0)
        {
            var target = _filesCurrentPath;
            if (string.IsNullOrWhiteSpace(target) || !IsPathWithinRoot(target, root))
            {
                target = root;
            }

            _ = LoadFilesDirectoryAsync(target, forceRefresh: true);
            return;
        }

        if (_filesLoadingGeneration == 0)
        {
            _filesView.SetReady(
                CanNavigateFilesUp(root),
                canRefresh: true,
                canLoadMore: _filesNextCursor is not null);
        }
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
            ResetFilesPaging(clearPath: true);
        }

        if (_filesLoadingGeneration != 0)
        {
            return;
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
        _filesLoadingGeneration = 0;
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

    private async void FilesView_LoadMoreRequested(object? sender, EventArgs e)
    {
        var current = _filesCurrentPath;
        var cursor = _filesNextCursor;
        if (current is null || cursor is null || _filesLoadingGeneration != 0)
        {
            return;
        }

        await LoadFilesPageAsync(current, cursor, append: true);
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
            ResetFilesPaging(clearPath: true);
            forceRefresh = true;
        }

        var samePath = _filesCurrentPath is not null && PathsEqual(_filesCurrentPath, directoryPath);
        if (!forceRefresh && samePath && _filesLoadedForSource)
        {
            ApplyFilesPageCache(root);
            return;
        }

        if (forceRefresh || !samePath)
        {
            _filesRows.Clear();
            _filesNextCursor = null;
            _filesCurrentTotalCount = 0;
            _filesLoadedForSource = false;
        }

        await LoadFilesPageAsync(directoryPath, cursor: null, append: false);
    }

    private async Task LoadFilesPageAsync(
        string directoryPath,
        FileDirectoryBrowseCursor? cursor,
        bool append)
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
            cursor = null;
            append = false;
        }

        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
        if (!string.Equals(sourceKey, _filesSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _filesSourceKey = sourceKey;
            ResetFilesPaging(clearPath: true);
            directoryPath = root;
            cursor = null;
            append = false;
        }

        if (append &&
            (!_filesLoadedForSource ||
             _filesCurrentPath is null ||
             !PathsEqual(_filesCurrentPath, directoryPath) ||
             _filesNextCursor != cursor))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _filesGeneration);
        _filesLoadingGeneration = generation;
        _filesView.SetLoading(
            directoryPath,
            append
                ? "Loading more exact directory entries…"
                : "Loading exact directory entries from the shared index…",
            preserveRows: append);
        SetFilesStatus(
            append
                ? $"Loading more entries from {directoryPath}…"
                : $"Loading {directoryPath} from the exact indexed browse API…");

        try
        {
            await _storageGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed || !_filesVisible || generation != Volatile.Read(ref _filesGeneration))
                {
                    return;
                }

                var page = await _searchEngine.BrowseDirectoryAsync(
                    directoryPath,
                    FilesPageSize,
                    cursor);
                if (_closed || !_filesVisible || generation != Volatile.Read(ref _filesGeneration))
                {
                    return;
                }

                ApplyFilesPage(page, root, append);
                _filesLoadingGeneration = 0;
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
                _filesLoadingGeneration = 0;
                var message = "The shared index is busy. Refresh Files after the current maintenance operation completes.";
                _filesView.SetStatus(message);
                SetFilesStatus(message);
            }
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.SnapshotRequired)
        {
            if (!_closed && _filesVisible && generation == Volatile.Read(ref _filesGeneration))
            {
                _filesLoadingGeneration = 0;
                var message = "The indexed namespace is refreshing. Files will be available when the snapshot is current again.";
                _filesView.SetStatus(message);
                SetFilesStatus(message);
            }
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.InvalidRequest)
        {
            if (!_closed && _filesVisible && generation == Volatile.Read(ref _filesGeneration))
            {
                _filesLoadingGeneration = 0;
                var message = "That directory is no longer present in the current index. Refresh or navigate to another folder.";
                _filesView.SetStatus(message);
                SetFilesStatus(message);
            }
        }
        catch (Exception exception)
        {
            if (!_closed && _filesVisible && generation == Volatile.Read(ref _filesGeneration))
            {
                _filesLoadingGeneration = 0;
                var message = $"Files could not be loaded: {exception.Message}";
                _filesView.SetStatus(message);
                SetFilesStatus(message);
            }
        }
        finally
        {
            if (!_closed &&
                _filesVisible &&
                generation == Volatile.Read(ref _filesGeneration) &&
                _filesLoadingGeneration == generation)
            {
                _filesLoadingGeneration = 0;
            }

            if (!_closed && _filesVisible && generation == Volatile.Read(ref _filesGeneration))
            {
                _filesView.SetReady(
                    CanNavigateFilesUp(root),
                    !_searchEngine.State.IsBusy,
                    _filesLoadedForSource && _filesNextCursor is not null);
            }
        }
    }

    private void ApplyFilesPage(FileDirectoryBrowsePage page, string root, bool append)
    {
        if (!append)
        {
            _filesRows.Clear();
        }

        HashSet<string>? existingPaths = append
            ? new HashSet<string>(_filesRows.Select(static row => row.Path), StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var record in page.Entries)
        {
            if (existingPaths is not null && !existingPaths.Add(record.Path))
            {
                continue;
            }

            _filesRows.Add(FileBrowserRow.FromRecord(record));
        }

        _filesCurrentPath = page.DirectoryPath;
        _filesCurrentTotalCount = page.TotalCount;
        _filesNextCursor = page.NextCursor;
        _filesLoadedForSource = true;
        ApplyFilesPageCache(root);
    }

    private void ApplyFilesPageCache(string root)
    {
        if (_filesCurrentPath is null)
        {
            return;
        }

        _filesView.Apply(
            _filesRows,
            _filesCurrentPath,
            _filesCurrentTotalCount,
            _filesNextCursor is not null,
            CanNavigateFilesUp(root),
            canRefresh: !_searchEngine.State.IsBusy);

        var source = _searchEngine.State.Mode == DesktopSearchMode.Native
            ? "whole-volume native index"
            : "profile fallback snapshot";
        var pageState = _filesNextCursor is null
            ? "exact page sequence complete"
            : "more exact pages available";
        SetFilesStatus(
            $"{_filesRows.Count:N0} loaded · current direct count {_filesCurrentTotalCount:N0} · {source} · {pageState}");
    }

    private void ResetFilesPaging(bool clearPath)
    {
        _filesRows.Clear();
        _filesNextCursor = null;
        _filesCurrentTotalCount = 0;
        _filesLoadedForSource = false;
        _filesLoadingGeneration = 0;
        if (clearPath)
        {
            _filesCurrentPath = null;
        }
    }

    private void UpdateFilesSourceDescription(DesktopSearchMode mode)
    {
        _filesView.SetSourceDescription(mode switch
        {
            DesktopSearchMode.Native =>
                "Browse exact direct children from the same whole-volume NTFS metadata index used by Search and Storage. Pages are keyset-ordered and no directory rescan is performed.",
            DesktopSearchMode.Fallback =>
                "Browse exact direct children from the completed user-profile fallback snapshot. Paging reads that in-memory snapshot and does not rescan the filesystem.",
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
