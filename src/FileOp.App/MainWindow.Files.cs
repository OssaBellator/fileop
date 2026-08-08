using FileOp.Core.Indexing.Service;
using FileOp.Core.Models;
using FileOp.Windows.IndexingService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MainWindow
{
    private const int FilesPageSize = 256;

    private readonly FilesPaneState _leftFilesPane = new("Left");
    private readonly FilesPaneState _rightFilesPane = new("Right");
    private FilesView _filesView = null!;
    private Button _filesNavigationButton = null!;
    private string? _filesSourceKey;
    private string _lastFilesStatus = string.Empty;
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

        EnsureFilesPaneHasTab(_leftFilesPane, path: null);
        EnsureFilesPaneHasTab(_rightFilesPane, path: null);
        ApplyFilesTabs(_leftFilesPane);
        ApplyFilesTabs(_rightFilesPane);

        _filesInitialized = true;
        _filesNavigationButton.Click += FilesNavigationButton_Click;
        SearchNavigationButton.Click += FilesOtherNavigationButton_Click;
        StorageNavigationButton.Click += FilesOtherNavigationButton_Click;
        SubscribeFilesPane(_filesView.LeftPane);
        SubscribeFilesPane(_filesView.RightPane);
        _searchEngine.StateChanged += FilesEngine_StateChanged;
        Closed += FilesWindow_Closed;

        HandleFilesEngineState(_searchEngine.State);
    }

    private void SubscribeFilesPane(FilesPaneView paneView)
    {
        paneView.UpRequested += FilesPane_UpRequested;
        paneView.RefreshRequested += FilesPane_RefreshRequested;
        paneView.LoadMoreRequested += FilesPane_LoadMoreRequested;
        paneView.NewTabRequested += FilesPane_NewTabRequested;
        paneView.CloseTabRequested += FilesPane_CloseTabRequested;
        paneView.TabRequested += FilesPane_TabRequested;
        paneView.EntryInvoked += FilesPane_EntryInvoked;
    }

    private void UnsubscribeFilesPane(FilesPaneView paneView)
    {
        paneView.UpRequested -= FilesPane_UpRequested;
        paneView.RefreshRequested -= FilesPane_RefreshRequested;
        paneView.LoadMoreRequested -= FilesPane_LoadMoreRequested;
        paneView.NewTabRequested -= FilesPane_NewTabRequested;
        paneView.CloseTabRequested -= FilesPane_CloseTabRequested;
        paneView.TabRequested -= FilesPane_TabRequested;
        paneView.EntryInvoked -= FilesPane_EntryInvoked;
    }

    private void FilesWindow_Closed(object sender, WindowEventArgs args)
    {
        _searchEngine.StateChanged -= FilesEngine_StateChanged;
        _filesNavigationButton.Click -= FilesNavigationButton_Click;
        SearchNavigationButton.Click -= FilesOtherNavigationButton_Click;
        StorageNavigationButton.Click -= FilesOtherNavigationButton_Click;
        UnsubscribeFilesPane(_filesView.LeftPane);
        UnsubscribeFilesPane(_filesView.RightPane);
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
            ResetAllFilesTabs(clearPath: true);
            InvalidateFilesPane(_leftFilesPane);
            InvalidateFilesPane(_rightFilesPane);
            if (_filesVisible)
            {
                SetAllFilesPanesUnavailable("Indexed Files browsing will be available after the search source is ready.");
                SetFilesStatus("Indexed Files browsing is currently unavailable.");
            }
            return;
        }

        var sourceKey = CreateStorageSourceKey(state.Mode, root);
        var sourceChanged = !string.Equals(sourceKey, _filesSourceKey, StringComparison.OrdinalIgnoreCase);
        if (sourceChanged)
        {
            _filesSourceKey = sourceKey;
            ResetAllFilesTabs(clearPath: true);
            InvalidateFilesPane(_leftFilesPane);
            InvalidateFilesPane(_rightFilesPane);
        }

        if (!_filesVisible)
        {
            return;
        }

        if (!sourceAvailable)
        {
            if (state.IsBusy)
            {
                ResetAllFilesTabs(clearPath: false);
                InvalidateFilesPane(_leftFilesPane);
                InvalidateFilesPane(_rightFilesPane);
            }

            SetAllFilesPanesUnavailable("Indexed Files browsing is temporarily unavailable while the shared index is changing.");
            SetFilesStatus("Files will be available when indexing is ready.");
            return;
        }

        _ = EnsureFilesPaneLoadedAsync(_leftFilesPane, root, sourceChanged);
        _ = EnsureFilesPaneLoadedAsync(_rightFilesPane, root, sourceChanged);
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
            SetAllFilesPanesUnavailable("Indexed Files browsing will be available when indexing is ready.");
            SetFilesStatus("Files will be available when indexing is ready.");
            return;
        }

        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
        if (!string.Equals(sourceKey, _filesSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _filesSourceKey = sourceKey;
            ResetAllFilesTabs(clearPath: true);
            InvalidateFilesPane(_leftFilesPane);
            InvalidateFilesPane(_rightFilesPane);
        }

        await Task.WhenAll(
            EnsureFilesPaneLoadedAsync(_leftFilesPane, root, forceRefresh: false),
            EnsureFilesPaneLoadedAsync(_rightFilesPane, root, forceRefresh: false));
    }

    private void FilesOtherNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_filesVisible)
        {
            return;
        }

        _filesVisible = false;
        InvalidateFilesPane(_leftFilesPane);
        InvalidateFilesPane(_rightFilesPane);
        _filesView.Visibility = Visibility.Collapsed;
    }

    private async void FilesPane_UpRequested(object? sender, EventArgs e)
    {
        if (!TryGetFilesPane(sender, out var pane, out _))
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        var current = pane.ActiveTab.CurrentPath;
        if (root is null || current is null || PathsEqual(root, current))
        {
            return;
        }

        var parent = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !IsPathWithinRoot(parent, root))
        {
            parent = root;
        }

        await LoadFilesDirectoryAsync(pane, parent, forceRefresh: false);
    }

    private async void FilesPane_RefreshRequested(object? sender, EventArgs e)
    {
        if (!TryGetFilesPane(sender, out var pane, out var paneView))
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null)
        {
            paneView.SetUnavailable("Indexed Files browsing is not currently available.");
            return;
        }

        var path = pane.ActiveTab.CurrentPath;
        if (string.IsNullOrWhiteSpace(path) || !IsPathWithinRoot(path, root))
        {
            path = root;
        }

        await LoadFilesDirectoryAsync(pane, path, forceRefresh: true);
    }

    private async void FilesPane_LoadMoreRequested(object? sender, EventArgs e)
    {
        if (!TryGetFilesPane(sender, out var pane, out _))
        {
            return;
        }

        var tab = pane.ActiveTab;
        var current = tab.CurrentPath;
        var cursor = tab.NextCursor;
        if (current is null || cursor is null || pane.LoadingGeneration != 0)
        {
            return;
        }

        await LoadFilesPageAsync(pane, tab, current, cursor, append: true);
    }

    private async void FilesPane_NewTabRequested(object? sender, EventArgs e)
    {
        if (!TryGetFilesPane(sender, out var pane, out _))
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            return;
        }

        var current = pane.ActiveTab.CurrentPath;
        var path = !string.IsNullOrWhiteSpace(current) && IsPathWithinRoot(current, root)
            ? current
            : root;
        var tab = CreateFilesTab(path);
        pane.Tabs.Add(tab);
        pane.ActiveTabId = tab.Id;
        InvalidateFilesPane(pane);
        ApplyFilesTabs(pane);
        await LoadFilesDirectoryAsync(pane, path, forceRefresh: true);
    }

    private async void FilesPane_CloseTabRequested(object? sender, EventArgs e)
    {
        if (!TryGetFilesPane(sender, out var pane, out _) || pane.Tabs.Count <= 1)
        {
            return;
        }

        var activeIndex = pane.Tabs.FindIndex(tab => tab.Id == pane.ActiveTabId);
        if (activeIndex < 0)
        {
            activeIndex = 0;
        }

        pane.Tabs.RemoveAt(activeIndex);
        pane.ActiveTabId = pane.Tabs[Math.Min(activeIndex, pane.Tabs.Count - 1)].Id;
        InvalidateFilesPane(pane);
        ApplyFilesTabs(pane);

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            return;
        }

        await EnsureFilesPaneLoadedAsync(pane, root, forceRefresh: false);
    }

    private async void FilesPane_TabRequested(object? sender, FileBrowserTabRequestedEventArgs e)
    {
        if (!TryGetFilesPane(sender, out var pane, out _) || pane.ActiveTabId == e.TabId)
        {
            return;
        }

        if (pane.Tabs.All(tab => tab.Id != e.TabId))
        {
            return;
        }

        pane.ActiveTabId = e.TabId;
        InvalidateFilesPane(pane);
        ApplyFilesTabs(pane);

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            return;
        }

        await EnsureFilesPaneLoadedAsync(pane, root, forceRefresh: false);
    }

    private async void FilesPane_EntryInvoked(object? sender, FileBrowserRow row)
    {
        if (!TryGetFilesPane(sender, out var pane, out _))
        {
            return;
        }

        if (row.IsDirectory)
        {
            await LoadFilesDirectoryAsync(pane, row.Path, forceRefresh: false);
            return;
        }

        OpenPath(row.Path, SetFilesStatus);
    }

    private async Task EnsureFilesPaneLoadedAsync(FilesPaneState pane, string root, bool forceRefresh)
    {
        EnsureFilesPaneHasTab(pane, root);
        ApplyFilesTabs(pane);

        if (pane.LoadingGeneration != 0)
        {
            return;
        }

        var tab = pane.ActiveTab;
        var path = tab.CurrentPath;
        if (string.IsNullOrWhiteSpace(path) || !IsPathWithinRoot(path, root))
        {
            path = root;
        }

        if (!forceRefresh && tab.LoadedForSource)
        {
            ApplyFilesPageCache(pane, root);
            return;
        }

        await LoadFilesDirectoryAsync(pane, path, forceRefresh: true);
    }

    private async Task LoadFilesDirectoryAsync(FilesPaneState pane, string directoryPath, bool forceRefresh)
    {
        if (_closed || !_filesVisible)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            GetFilesPaneView(pane).SetUnavailable("Indexed Files browsing is not currently available.");
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
            ResetAllFilesTabs(clearPath: true);
            InvalidateFilesPane(_leftFilesPane);
            InvalidateFilesPane(_rightFilesPane);
            directoryPath = root;
            forceRefresh = true;
        }

        EnsureFilesPaneHasTab(pane, directoryPath);
        var tab = pane.ActiveTab;
        var samePath = tab.CurrentPath is not null && PathsEqual(tab.CurrentPath, directoryPath);
        if (!forceRefresh && samePath && tab.LoadedForSource)
        {
            ApplyFilesPageCache(pane, root);
            return;
        }

        if (forceRefresh || !samePath)
        {
            ResetFilesTabPaging(tab, clearPath: false);
            tab.CurrentPath = directoryPath;
            ApplyFilesTabs(pane);
        }

        await LoadFilesPageAsync(pane, tab, directoryPath, cursor: null, append: false);
    }

    private async Task LoadFilesPageAsync(
        FilesPaneState pane,
        FilesTabState tab,
        string directoryPath,
        FileDirectoryBrowseCursor? cursor,
        bool append)
    {
        if (_closed || !_filesVisible || pane.ActiveTabId != tab.Id)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            GetFilesPaneView(pane).SetUnavailable("Indexed Files browsing is not currently available.");
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
            ResetAllFilesTabs(clearPath: true);
            InvalidateFilesPane(_leftFilesPane);
            InvalidateFilesPane(_rightFilesPane);
            EnsureFilesPaneHasTab(pane, root);
            tab = pane.ActiveTab;
            tab.CurrentPath = root;
            directoryPath = root;
            cursor = null;
            append = false;
        }

        if (append &&
            (!tab.LoadedForSource ||
             tab.CurrentPath is null ||
             !PathsEqual(tab.CurrentPath, directoryPath) ||
             tab.NextCursor != cursor))
        {
            return;
        }

        var generation = ++pane.Generation;
        pane.LoadingGeneration = generation;
        var paneView = GetFilesPaneView(pane);
        paneView.SetLoading(
            directoryPath,
            append
                ? "Loading more exact directory entries…"
                : "Loading exact directory entries from the shared index…",
            preserveRows: append);
        SetFilesStatus(
            append
                ? $"{pane.Name}: loading more entries from {directoryPath}…"
                : $"{pane.Name}: loading {directoryPath} from the exact indexed browse API…");

        try
        {
            await _storageGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (!IsFilesRequestCurrent(pane, tab, generation))
                {
                    return;
                }

                var page = await _searchEngine.BrowseDirectoryAsync(
                    directoryPath,
                    FilesPageSize,
                    cursor);
                if (!IsFilesRequestCurrent(pane, tab, generation))
                {
                    return;
                }

                ApplyFilesPage(pane, tab, page, root, append);
                pane.LoadingGeneration = 0;
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
            ApplyFilesLoadError(pane, tab, generation,
                "The shared index is busy. Refresh this pane after the current maintenance operation completes.");
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.SnapshotRequired)
        {
            ApplyFilesLoadError(pane, tab, generation,
                "The indexed namespace is refreshing. This pane will be available when the snapshot is current again.");
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.InvalidRequest)
        {
            ApplyFilesLoadError(pane, tab, generation,
                "That directory is no longer present in the current index. Refresh or navigate to another folder.");
        }
        catch (Exception exception)
        {
            ApplyFilesLoadError(pane, tab, generation, $"Files could not be loaded: {exception.Message}");
        }
        finally
        {
            if (IsFilesRequestCurrent(pane, tab, generation) && pane.LoadingGeneration == generation)
            {
                pane.LoadingGeneration = 0;
            }

            if (IsFilesRequestCurrent(pane, tab, generation))
            {
                paneView.SetReady(
                    CanNavigateFilesUp(tab, root),
                    !_searchEngine.State.IsBusy,
                    tab.LoadedForSource && tab.NextCursor is not null);
                ApplyFilesTabs(pane);
            }
        }
    }

    private void ApplyFilesLoadError(FilesPaneState pane, FilesTabState tab, int generation, string message)
    {
        if (!IsFilesRequestCurrent(pane, tab, generation))
        {
            return;
        }

        pane.LoadingGeneration = 0;
        GetFilesPaneView(pane).SetStatus(message, tab.CurrentPath);
        SetFilesStatus($"{pane.Name}: {message}");
    }

    private bool IsFilesRequestCurrent(FilesPaneState pane, FilesTabState tab, int generation) =>
        !_closed &&
        _filesVisible &&
        pane.ActiveTabId == tab.Id &&
        pane.Generation == generation;

    private void ApplyFilesPage(
        FilesPaneState pane,
        FilesTabState tab,
        FileDirectoryBrowsePage page,
        string root,
        bool append)
    {
        if (!append)
        {
            tab.Rows.Clear();
        }

        HashSet<string>? existingPaths = append
            ? new HashSet<string>(tab.Rows.Select(static row => row.Path), StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var record in page.Entries)
        {
            if (existingPaths is not null && !existingPaths.Add(record.Path))
            {
                continue;
            }

            tab.Rows.Add(FileBrowserRow.FromRecord(record));
        }

        tab.CurrentPath = page.DirectoryPath;
        tab.CurrentTotalCount = page.TotalCount;
        tab.NextCursor = page.NextCursor;
        tab.LoadedForSource = true;
        ApplyFilesTabs(pane);
        ApplyFilesPageCache(pane, root);
    }

    private void ApplyFilesPageCache(FilesPaneState pane, string root)
    {
        var tab = pane.ActiveTab;
        if (tab.CurrentPath is null)
        {
            return;
        }

        GetFilesPaneView(pane).Apply(
            tab.Rows,
            tab.CurrentPath,
            tab.CurrentTotalCount,
            tab.NextCursor is not null,
            CanNavigateFilesUp(tab, root),
            canRefresh: !_searchEngine.State.IsBusy);

        var source = _searchEngine.State.Mode == DesktopSearchMode.Native
            ? "whole-volume native index"
            : "profile fallback snapshot";
        var pageState = tab.NextCursor is null
            ? "exact page sequence complete"
            : "more exact pages available";
        SetFilesStatus(
            $"{pane.Name}: {tab.Rows.Count:N0} loaded · current direct count {tab.CurrentTotalCount:N0} · {source} · {pageState}");
    }

    private void SetAllFilesPanesUnavailable(string message)
    {
        _filesView.LeftPane.SetUnavailable(message);
        _filesView.RightPane.SetUnavailable(message);
        ApplyFilesTabs(_leftFilesPane);
        ApplyFilesTabs(_rightFilesPane);
    }

    private void ResetAllFilesTabs(bool clearPath)
    {
        foreach (var pane in EnumerateFilesPanes())
        {
            EnsureFilesPaneHasTab(pane, path: null);
            foreach (var tab in pane.Tabs)
            {
                ResetFilesTabPaging(tab, clearPath);
            }
            ApplyFilesTabs(pane);
        }
    }

    private static void ResetFilesTabPaging(FilesTabState tab, bool clearPath)
    {
        tab.Rows.Clear();
        tab.CurrentTotalCount = 0;
        tab.NextCursor = null;
        tab.LoadedForSource = false;
        if (clearPath)
        {
            tab.CurrentPath = null;
        }
    }

    private void InvalidateFilesPane(FilesPaneState pane)
    {
        pane.Generation++;
        pane.LoadingGeneration = 0;
    }

    private void EnsureFilesPaneHasTab(FilesPaneState pane, string? path)
    {
        if (pane.Tabs.Count != 0)
        {
            if (pane.Tabs.All(tab => tab.Id != pane.ActiveTabId))
            {
                pane.ActiveTabId = pane.Tabs[0].Id;
            }
            return;
        }

        var tab = CreateFilesTab(path);
        pane.Tabs.Add(tab);
        pane.ActiveTabId = tab.Id;
    }

    private static FilesTabState CreateFilesTab(string? path) =>
        new(Guid.NewGuid())
        {
            CurrentPath = path,
        };

    private void ApplyFilesTabs(FilesPaneState pane)
    {
        EnsureFilesPaneHasTab(pane, path: null);
        var headers = pane.Tabs
            .Select((tab, index) => new FileBrowserTabHeader(
                tab.Id,
                CreateFilesTabTitle(tab.CurrentPath, index),
                tab.CurrentPath ?? string.Empty))
            .ToArray();
        GetFilesPaneView(pane).ApplyTabs(headers, pane.ActiveTabId);
    }

    private static string CreateFilesTabTitle(string? path, int index)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"Tab {index + 1}";
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private bool TryGetFilesPane(object? sender, out FilesPaneState pane, out FilesPaneView paneView)
    {
        if (ReferenceEquals(sender, _filesView.LeftPane))
        {
            pane = _leftFilesPane;
            paneView = _filesView.LeftPane;
            return true;
        }

        if (ReferenceEquals(sender, _filesView.RightPane))
        {
            pane = _rightFilesPane;
            paneView = _filesView.RightPane;
            return true;
        }

        pane = null!;
        paneView = null!;
        return false;
    }

    private FilesPaneView GetFilesPaneView(FilesPaneState pane) =>
        ReferenceEquals(pane, _leftFilesPane)
            ? _filesView.LeftPane
            : _filesView.RightPane;

    private IEnumerable<FilesPaneState> EnumerateFilesPanes()
    {
        yield return _leftFilesPane;
        yield return _rightFilesPane;
    }

    private void UpdateFilesSourceDescription(DesktopSearchMode mode)
    {
        _filesView.SetSourceDescription(mode switch
        {
            DesktopSearchMode.Native =>
                "Browse two independent tabbed panes from the same whole-volume NTFS metadata index used by Search and Storage. Pages are keyset-ordered and serialized through the shared indexed browse session.",
            DesktopSearchMode.Fallback =>
                "Browse two independent tabbed panes from the completed user-profile fallback snapshot. Paging reads that in-memory snapshot and does not rescan the filesystem.",
            _ =>
                "Indexed directory browsing becomes available when the shared search source is ready.",
        });
    }

    private bool CanNavigateFilesUp(FilesTabState tab, string root) =>
        !_closed &&
        !_searchEngine.State.IsBusy &&
        tab.CurrentPath is not null &&
        !PathsEqual(root, tab.CurrentPath);

    private void SetFilesStatus(string status)
    {
        _lastFilesStatus = status;
        if (_filesVisible)
        {
            SearchStatusText.Text = status;
        }
    }

    private sealed class FilesPaneState
    {
        public FilesPaneState(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public List<FilesTabState> Tabs { get; } = [];

        public Guid ActiveTabId { get; set; }

        public int Generation { get; set; }

        public int LoadingGeneration { get; set; }

        public FilesTabState ActiveTab =>
            Tabs.First(tab => tab.Id == ActiveTabId);
    }

    private sealed class FilesTabState
    {
        public FilesTabState(Guid id)
        {
            Id = id;
        }

        public Guid Id { get; }

        public string? CurrentPath { get; set; }

        public List<FileBrowserRow> Rows { get; } = [];

        public int CurrentTotalCount { get; set; }

        public FileDirectoryBrowseCursor? NextCursor { get; set; }

        public bool LoadedForSource { get; set; }
    }
}
