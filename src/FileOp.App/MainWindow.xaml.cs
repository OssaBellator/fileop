using System.Collections.ObjectModel;
using System.Diagnostics;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Models;
using FileOp.Core.Services;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FileOp.App;

public sealed partial class MainWindow : Window
{
    private const int StorageAnalysisEntryLimit = 256;
    private const int MaximumTreemapTiles = 48;

    private readonly DesktopSearchEngine _searchEngine = new();
    private readonly StorageSnapshotService _storageSnapshotService = new();
    private readonly ObservableCollection<SearchResultRow> _results = [];
    private readonly ObservableCollection<VolumeCard> _volumes = [];
    private readonly ObservableCollection<StorageEntryRow> _storageEntries = [];
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private AppSection _activeSection = AppSection.Search;
    private StorageDirectoryAnalysis? _storageAnalysis;
    private string? _storageCurrentPath;
    private string? _storageSourceKey;
    private string _lastSearchStatus = string.Empty;
    private string _lastStorageStatus = string.Empty;
    private int _searchGeneration;
    private int _storageGeneration;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        ResultsList.ItemsSource = _results;
        VolumeCards.ItemsSource = _volumes;
        StorageList.ItemsSource = _storageEntries;

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(140);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += SearchTimer_Tick;

        _searchEngine.StateChanged += SearchEngine_StateChanged;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
        ShowSection(AppSection.Search);
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        LoadVolumes();

        var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ApplyEngineState(new DesktopSearchEngineState(
                DesktopSearchMode.Unavailable,
                "User profile is unavailable.",
                0,
                IsBusy: false,
                CanElevate: false,
                IsCurrent: false));
            return;
        }

        try
        {
            await _searchEngine.InitializeAsync(root, _lifetimeCancellation.Token);
            if (_closed)
            {
                return;
            }

            ApplyEngineState(_searchEngine.State);
            if (_activeSection == AppSection.Search && SearchBox.IsEnabled)
            {
                await RunSearchAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplyEngineState(new DesktopSearchEngineState(
                DesktopSearchMode.Unavailable,
                $"Search engine could not start: {exception.Message}",
                0,
                IsBusy: false,
                CanElevate: false,
                IsCurrent: false));
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _searchTimer.Stop();
        _lifetimeCancellation.Cancel();
        _searchEngine.StateChanged -= SearchEngine_StateChanged;

        var disposeTask = _searchEngine.DisposeAsync().AsTask();
        try
        {
            await _searchGate.WaitAsync(CancellationToken.None);
            _searchGate.Release();
            await _storageGate.WaitAsync(CancellationToken.None);
            _storageGate.Release();
            await disposeTask;
        }
        catch (Exception)
        {
            // Window shutdown should not be blocked by search/storage/helper teardown failures.
        }
        finally
        {
            _storageGate.Dispose();
            _searchGate.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private void LoadVolumes()
    {
        _volumes.Clear();
        foreach (var volume in _storageSnapshotService.GetReadyVolumes().Take(4))
        {
            _volumes.Add(VolumeCard.FromSnapshot(volume));
        }
    }

    private void SearchEngine_StateChanged(DesktopSearchEngineState state)
    {
        if (_closed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyEngineState(state);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed)
            {
                ApplyEngineState(state);
            }
        });
    }

    private void ApplyEngineState(DesktopSearchEngineState state)
    {
        EngineStatusText.Text = state.Status;
        IndexingProgressRing.IsActive = state.IsBusy;
        CountText.Text = state.IndexedItemCount > 0
            ? $"{state.IndexedItemCount:N0} indexed"
            : string.Empty;

        var sourceAvailable = !_closed &&
            !state.IsBusy &&
            state.Mode is DesktopSearchMode.Native or DesktopSearchMode.Fallback;
        SearchBox.IsEnabled = sourceAvailable;
        StorageRefreshButton.IsEnabled = sourceAvailable && _searchEngine.StorageRootPath is not null;

        EnableFastIndexButton.Visibility = state.CanElevate
            ? Visibility.Visible
            : Visibility.Collapsed;
        EnableFastIndexButton.IsEnabled = state.CanElevate && !state.IsBusy;
        EnableFastIndexButton.Content = state.Mode == DesktopSearchMode.Native
            ? "Enable live updates"
            : "Enable fast indexing";

        UpdateStorageScope();
        UpdateStorageNavigationState();

        if (_activeSection != AppSection.Storage || !sourceAvailable)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null)
        {
            return;
        }

        var sourceKey = CreateStorageSourceKey(state.Mode, root);
        if (!string.Equals(sourceKey, _storageSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _storageSourceKey = sourceKey;
            _storageCurrentPath = null;
            _storageAnalysis = null;
            _storageEntries.Clear();
            ResetStorageSummary();
            _ = RunStorageAnalysisAsync(root);
        }
    }

    private void SearchNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(AppSection.Search);
    }

    private async void StorageNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(AppSection.Storage);
        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            SetStorageStatus("Storage analysis will be available when indexing is ready.");
            return;
        }

        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
        if (!string.Equals(sourceKey, _storageSourceKey, StringComparison.OrdinalIgnoreCase) ||
            _storageAnalysis is null)
        {
            _storageSourceKey = sourceKey;
            await RunStorageAnalysisAsync(root);
        }
        else
        {
            RenderStorageTreemap();
        }
    }

    private void ShowSection(AppSection section)
    {
        _activeSection = section;
        SearchView.Visibility = section == AppSection.Search ? Visibility.Visible : Visibility.Collapsed;
        StorageView.Visibility = section == AppSection.Storage ? Visibility.Visible : Visibility.Collapsed;
        SearchStatusText.Text = section == AppSection.Search ? _lastSearchStatus : _lastStorageStatus;

        if (section == AppSection.Storage)
        {
            UpdateStorageScope();
            UpdateStorageNavigationState();
            RenderStorageTreemap();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Interlocked.Increment(ref _searchGeneration);
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void SearchTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        if (!SearchBox.IsEnabled || _closed)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _searchGeneration);
        var rawQuery = SearchBox.Text;

        try
        {
            await _searchGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed || generation != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }

                var matches = await _searchEngine.SearchAsync(rawQuery, limit: 250);
                if (_closed || generation != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }

                _results.Clear();
                foreach (var match in matches)
                {
                    _results.Add(SearchResultRow.FromRecord(match));
                }

                var mode = _searchEngine.State.Mode == DesktopSearchMode.Native
                    ? "native index"
                    : "profile fallback";
                SetSearchStatus(string.IsNullOrWhiteSpace(rawQuery)
                    ? $"Showing {matches.Count:N0} indexed item(s) · {mode}"
                    : $"{matches.Count:N0} result(s) · {mode}");
            }
            finally
            {
                _searchGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                SetSearchStatus($"Search failed: {exception.Message}");
            }
        }
    }

    private async Task RunStorageAnalysisAsync(string directoryPath)
    {
        if (_closed)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null)
        {
            SetStorageStatus("Storage analysis is not currently available.");
            return;
        }

        if (!IsPathWithinRoot(directoryPath, root))
        {
            directoryPath = root;
        }

        var generation = Interlocked.Increment(ref _storageGeneration);
        SetStorageStatus($"Analyzing {directoryPath} from the shared index…");
        StorageRefreshButton.IsEnabled = false;
        StorageUpButton.IsEnabled = false;

        try
        {
            await _storageGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed || generation != Volatile.Read(ref _storageGeneration))
                {
                    return;
                }

                var analysis = await _searchEngine.AnalyzeStorageAsync(
                    directoryPath,
                    StorageAnalysisEntryLimit);
                if (_closed || generation != Volatile.Read(ref _storageGeneration))
                {
                    return;
                }

                ApplyStorageAnalysis(analysis);
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
            if (!_closed && generation == Volatile.Read(ref _storageGeneration))
            {
                SetStorageStatus("Another FileOp session is maintaining this index. Refresh the analysis after that operation completes.");
            }
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.SnapshotRequired)
        {
            if (!_closed && generation == Volatile.Read(ref _storageGeneration))
            {
                SetStorageStatus("This storage snapshot is no longer valid. The indexing engine is refreshing it before analysis can continue.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed && generation == Volatile.Read(ref _storageGeneration))
            {
                SetStorageStatus($"Storage analysis failed: {exception.Message}");
            }
        }
        finally
        {
            if (!_closed && generation == Volatile.Read(ref _storageGeneration))
            {
                StorageRefreshButton.IsEnabled = !_searchEngine.State.IsBusy && _searchEngine.StorageRootPath is not null;
                UpdateStorageNavigationState();
            }
        }
    }

    private void ApplyStorageAnalysis(StorageDirectoryAnalysis analysis)
    {
        _storageAnalysis = analysis;
        _storageCurrentPath = analysis.RootPath;
        StoragePathText.Text = analysis.RootPath;
        StorageLogicalText.Text = ByteFormatter.Format(analysis.LogicalBytes);
        StorageAllocatedText.Text = analysis.AllocatedBytes is { } allocated
            ? ByteFormatter.Format(allocated)
            : "Unknown";
        StorageFilesText.Text = $"{analysis.UniqueFileCount:N0}";
        StorageAliasesText.Text = $"{analysis.HardLinkAliasCount:N0}";

        var usePhysicalWeights = analysis.AllocatedBytes.HasValue;
        StorageTreemapModeText.Text = usePhysicalWeights
            ? "Physical allocation"
            : "Logical size · allocation incomplete";

        _storageEntries.Clear();
        foreach (var entry in analysis.Entries)
        {
            _storageEntries.Add(StorageEntryRow.FromEntry(entry, usePhysicalWeights));
        }

        var scope = _searchEngine.StorageCoversWholeVolume
            ? "whole-volume native index"
            : "profile fallback snapshot";
        var displayed = analysis.Entries.Count;
        var aliases = analysis.HardLinkAliasCount > 0
            ? $" · {analysis.HardLinkAliasCount:N0} hard-link alias(es)"
            : string.Empty;
        SetStorageStatus(
            $"{displayed:N0} of {analysis.DirectEntryCount:N0} direct entries · " +
            $"{analysis.UniqueFileCount:N0} unique files{aliases} · {scope}");

        UpdateStorageNavigationState();
        RenderStorageTreemap();
    }

    private async void StorageRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var root = _searchEngine.StorageRootPath;
        if (root is null)
        {
            SetStorageStatus("Storage analysis is not currently available.");
            return;
        }

        var path = _storageCurrentPath;
        if (string.IsNullOrWhiteSpace(path) || !IsPathWithinRoot(path, root))
        {
            path = root;
        }

        await RunStorageAnalysisAsync(path);
    }

    private async void StorageUpButton_Click(object sender, RoutedEventArgs e)
    {
        var root = _searchEngine.StorageRootPath;
        var current = _storageCurrentPath;
        if (root is null || current is null || PathsEqual(root, current))
        {
            return;
        }

        var parent = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !IsPathWithinRoot(parent, root))
        {
            parent = root;
        }

        await RunStorageAnalysisAsync(parent);
    }

    private async void StorageList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not StorageEntryRow row)
        {
            return;
        }

        if (row.IsDirectory)
        {
            await RunStorageAnalysisAsync(row.Path);
            return;
        }

        OpenPath(row.Path, SetStorageStatus);
    }

    private async void StorageTreemapTile_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: StorageEntryRow row })
        {
            return;
        }

        if (row.IsDirectory)
        {
            await RunStorageAnalysisAsync(row.Path);
            return;
        }

        OpenPath(row.Path, SetStorageStatus);
    }

    private void StorageTreemapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_activeSection == AppSection.Storage)
        {
            RenderStorageTreemap();
        }
    }

    private void RenderStorageTreemap()
    {
        StorageTreemapCanvas.Children.Clear();
        if (_storageAnalysis is null || _storageEntries.Count == 0)
        {
            StorageTreemapEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var width = StorageTreemapCanvas.ActualWidth;
        var height = StorageTreemapCanvas.ActualHeight;
        if (width < 8 || height < 8)
        {
            return;
        }

        var weightedRows = _storageEntries
            .Where(static row => row.TreemapBytes > 0)
            .OrderByDescending(static row => row.TreemapBytes)
            .ToArray();
        if (weightedRows.Length == 0)
        {
            StorageTreemapEmptyText.Visibility = Visibility.Visible;
            return;
        }

        StorageTreemapEmptyText.Visibility = Visibility.Collapsed;
        var items = new List<TreemapItem>(Math.Min(weightedRows.Length, MaximumTreemapTiles));
        if (weightedRows.Length <= MaximumTreemapTiles)
        {
            items.AddRange(weightedRows.Select(static row =>
                new TreemapItem(row, row.Name, row.TreemapBytes, row.TreemapSizeText)));
        }
        else
        {
            var visibleCount = MaximumTreemapTiles - 1;
            items.AddRange(weightedRows.Take(visibleCount).Select(static row =>
                new TreemapItem(row, row.Name, row.TreemapBytes, row.TreemapSizeText)));
            var otherWeight = weightedRows.Skip(visibleCount).Sum(static row => row.TreemapBytes);
            items.Add(new TreemapItem(
                null,
                $"Other {weightedRows.Length - visibleCount:N0} entries",
                otherWeight,
                ByteFormatter.Format(otherWeight)));
        }

        var rectangles = new List<TreemapRectangle>(items.Count);
        LayoutTreemap(items, 0, items.Count, 0, 0, width, height, 0, rectangles);

        var rootElement = Content as FrameworkElement;
        var tileStyle = rootElement?.Resources["StorageTreemapTileStyle"] as Style;
        foreach (var rectangle in rectangles)
        {
            if (rectangle.Width < 2 || rectangle.Height < 2)
            {
                continue;
            }

            var border = new Border
            {
                Width = Math.Max(0, rectangle.Width - 3),
                Height = Math.Max(0, rectangle.Height - 3),
                Style = tileStyle,
                Opacity = Math.Min(0.92, 0.72 + (rectangle.Depth % 4) * 0.05),
                Tag = rectangle.Item.Entry,
            };
            Canvas.SetLeft(border, rectangle.X + 1.5);
            Canvas.SetTop(border, rectangle.Y + 1.5);

            var content = new StackPanel { Spacing = 2 };
            if (rectangle.Width >= 72 && rectangle.Height >= 34)
            {
                content.Children.Add(new TextBlock
                {
                    Text = rectangle.Item.Name,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            if (rectangle.Width >= 96 && rectangle.Height >= 54)
            {
                content.Children.Add(new TextBlock
                {
                    Text = rectangle.Item.SizeText,
                    FontSize = 11,
                    Opacity = 0.75,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            border.Child = content;
            ToolTipService.SetToolTip(border, $"{rectangle.Item.Name}\n{rectangle.Item.SizeText}");
            if (rectangle.Item.Entry is not null)
            {
                border.Tapped += StorageTreemapTile_Tapped;
            }

            StorageTreemapCanvas.Children.Add(border);
        }
    }

    private static void LayoutTreemap(
        IReadOnlyList<TreemapItem> items,
        int start,
        int count,
        double x,
        double y,
        double width,
        double height,
        int depth,
        ICollection<TreemapRectangle> output)
    {
        if (count <= 0 || width <= 0 || height <= 0)
        {
            return;
        }

        if (count == 1)
        {
            output.Add(new TreemapRectangle(items[start], x, y, width, height, depth));
            return;
        }

        long total = 0;
        for (var index = start; index < start + count; index++)
        {
            total += items[index].Weight;
        }

        if (total <= 0)
        {
            return;
        }

        var target = total / 2d;
        long leftWeight = 0;
        var leftCount = 0;
        while (leftCount < count - 1)
        {
            var candidate = items[start + leftCount].Weight;
            if (leftCount > 0 && leftWeight + candidate > target)
            {
                break;
            }

            leftWeight += candidate;
            leftCount++;
        }

        if (leftCount == 0)
        {
            leftCount = 1;
            leftWeight = items[start].Weight;
        }

        var ratio = leftWeight / (double)total;
        if (width >= height)
        {
            var leftWidth = width * ratio;
            LayoutTreemap(items, start, leftCount, x, y, leftWidth, height, depth + 1, output);
            LayoutTreemap(
                items,
                start + leftCount,
                count - leftCount,
                x + leftWidth,
                y,
                width - leftWidth,
                height,
                depth + 1,
                output);
        }
        else
        {
            var topHeight = height * ratio;
            LayoutTreemap(items, start, leftCount, x, y, width, topHeight, depth + 1, output);
            LayoutTreemap(
                items,
                start + leftCount,
                count - leftCount,
                x,
                y + topHeight,
                width,
                height - topHeight,
                depth + 1,
                output);
        }
    }

    private void UpdateStorageScope()
    {
        StorageScopeText.Text = _searchEngine.State.Mode switch
        {
            DesktopSearchMode.Native =>
                "Whole-volume analysis from the shared NTFS index. Search and Storage use the same live metadata.",
            DesktopSearchMode.Fallback =>
                "Profile-only analysis from the existing crawler snapshot. No second filesystem scan is performed; physical allocation may be unknown.",
            _ => "Storage analysis becomes available after the indexing source is ready.",
        };
    }

    private void UpdateStorageNavigationState()
    {
        var root = _searchEngine.StorageRootPath;
        StorageUpButton.IsEnabled = !_closed &&
            !_searchEngine.State.IsBusy &&
            root is not null &&
            _storageCurrentPath is not null &&
            !PathsEqual(root, _storageCurrentPath);
    }

    private void ResetStorageSummary()
    {
        StoragePathText.Text = string.Empty;
        StorageLogicalText.Text = "—";
        StorageAllocatedText.Text = "—";
        StorageFilesText.Text = "—";
        StorageAliasesText.Text = "—";
        StorageTreemapModeText.Text = string.Empty;
        StorageTreemapCanvas.Children.Clear();
        StorageTreemapEmptyText.Visibility = Visibility.Visible;
    }

    private async void EnableFastIndexButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        var storageSourceBeforeElevation = _storageSourceKey;
        SearchBox.IsEnabled = false;
        StorageRefreshButton.IsEnabled = false;
        EnableFastIndexButton.IsEnabled = false;
        SetSearchStatus(string.Empty);
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Increment(ref _storageGeneration);

        try
        {
            await _searchEngine.TryElevateAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_closed)
            {
                ApplyEngineState(_searchEngine.State);
            }
        }

        if (_closed)
        {
            return;
        }

        if (_activeSection == AppSection.Search && SearchBox.IsEnabled)
        {
            await RunSearchAsync();
            return;
        }

        if (_activeSection == AppSection.Storage &&
            !_searchEngine.State.IsBusy &&
            _searchEngine.StorageRootPath is { } root)
        {
            var currentSourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
            if (string.Equals(
                    currentSourceKey,
                    storageSourceBeforeElevation,
                    StringComparison.OrdinalIgnoreCase))
            {
                var path = _storageCurrentPath;
                if (string.IsNullOrWhiteSpace(path) || !IsPathWithinRoot(path, root))
                {
                    path = root;
                }

                await RunStorageAnalysisAsync(path);
            }
        }
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultRow row)
        {
            OpenPath(row.Path, SetSearchStatus);
        }
    }

    private void SetSearchStatus(string status)
    {
        _lastSearchStatus = status;
        if (_activeSection == AppSection.Search)
        {
            SearchStatusText.Text = status;
        }
    }

    private void SetStorageStatus(string status)
    {
        _lastStorageStatus = status;
        if (_activeSection == AppSection.Storage)
        {
            SearchStatusText.Text = status;
        }
    }

    private static void OpenPath(string path, Action<string> reportStatus)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            reportStatus($"Could not open item: {exception.Message}");
        }
    }

    private static string CreateStorageSourceKey(DesktopSearchMode mode, string rootPath) =>
        $"{mode}:{Path.GetFullPath(rootPath)}";

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsPathWithinRoot(string path, string rootPath)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(rootPath);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private enum AppSection
    {
        Search,
        Storage,
    }

    private sealed record TreemapItem(
        StorageEntryRow? Entry,
        string Name,
        long Weight,
        string SizeText);

    private sealed record TreemapRectangle(
        TreemapItem Item,
        double X,
        double Y,
        double Width,
        double Height,
        int Depth);
}

public sealed record SearchResultRow(string Path, string Name, string ParentPath, string SizeText)
{
    public static SearchResultRow FromRecord(FileRecord record) => new(
        record.Path,
        record.Name,
        record.ParentPath,
        record.IsDirectory ? "Folder" : ByteFormatter.Format(record.Length));
}

public sealed record StorageEntryRow(
    string Path,
    string Name,
    bool IsDirectory,
    long LogicalBytes,
    long? AllocatedBytes,
    long TreemapBytes,
    int FileCount,
    int DirectoryCount,
    int HardLinkAliasCount,
    string LogicalText,
    string AllocatedText,
    string TreemapSizeText,
    string FileCountText,
    string DetailText)
{
    public static StorageEntryRow FromEntry(StorageDirectoryEntry entry, bool usePhysicalWeight)
    {
        var weight = usePhysicalWeight
            ? entry.AllocatedBytes ?? 0
            : entry.LogicalBytes;
        var detail = entry.IsDirectory
            ? $"{entry.UniqueFileCount:N0} unique files · {entry.DirectoryCount:N0} folder records"
            : entry.HardLinkAliasCount > 0
                ? "Hard-link alias · physical allocation attributed elsewhere"
                : entry.Path;

        return new StorageEntryRow(
            entry.Path,
            entry.Name,
            entry.IsDirectory,
            entry.LogicalBytes,
            entry.AllocatedBytes,
            weight,
            entry.FileCount,
            entry.DirectoryCount,
            entry.HardLinkAliasCount,
            ByteFormatter.Format(entry.LogicalBytes),
            entry.AllocatedBytes is { } allocated ? ByteFormatter.Format(allocated) : "Unknown",
            ByteFormatter.Format(weight),
            $"{entry.FileCount:N0}",
            detail);
    }
}

public sealed record VolumeCard(string Name, string Usage, double UsedPercent)
{
    public static VolumeCard FromSnapshot(StorageVolumeSnapshot snapshot)
    {
        var displayName = string.IsNullOrWhiteSpace(snapshot.Label)
            ? snapshot.Name
            : $"{snapshot.Name} {snapshot.Label}";

        return new VolumeCard(
            displayName,
            $"{ByteFormatter.Format(snapshot.UsedBytes)} / {ByteFormatter.Format(snapshot.TotalBytes)}",
            snapshot.UsedFraction * 100);
    }
}

internal static class ByteFormatter
{
    private static readonly string[] Suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        var value = Math.Max(0, (double)bytes);
        var suffix = 0;
        while (value >= 1024 && suffix < Suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0 ? $"{value:N0} {Suffixes[suffix]}" : $"{value:N1} {Suffixes[suffix]}";
    }
}
