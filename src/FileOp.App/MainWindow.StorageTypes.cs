using System.Collections.ObjectModel;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MainWindow
{
    private const int StorageTypeAnalysisLimit = 256;

    private readonly ObservableCollection<StorageFileTypeRow> _storageFileTypes = [];
    private readonly ObservableCollection<StorageCategoryRow> _storageCategories = [];
    private StorageFileTypeAnalysis? _storageFileTypeAnalysis;
    private string? _storageTypesPath;
    private string? _storageTypesSourceKey;
    private StorageViewMode _storageViewMode = StorageViewMode.Folders;
    private int _storageTypeGeneration;
    private bool _storageTypesInitialized;

    private void StorageTypesView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_storageTypesInitialized)
        {
            return;
        }

        _storageTypesInitialized = true;
        StorageTypesList.ItemsSource = _storageFileTypes;
        StorageCategoryList.ItemsSource = _storageCategories;

        // Run the Types-aware source transition first. When Types is visible it can
        // suppress the original folder-only source refresh without changing the
        // reviewed MainWindow.xaml.cs lifecycle implementation.
        _searchEngine.StateChanged -= SearchEngine_StateChanged;
        _searchEngine.StateChanged += StorageTypesEngine_StateChanged;
        _searchEngine.StateChanged += SearchEngine_StateChanged;
        Closed += StorageTypesWindow_Closed;

        SetStorageViewMode(StorageViewMode.Folders);
        HandleStorageTypesEngineState(_searchEngine.State);
    }

    private void StorageTypesWindow_Closed(object sender, WindowEventArgs args)
    {
        _searchEngine.StateChanged -= StorageTypesEngine_StateChanged;
        Closed -= StorageTypesWindow_Closed;
    }

    private void StorageTypesEngine_StateChanged(DesktopSearchEngineState state)
    {
        if (_closed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            HandleStorageTypesEngineState(state);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed)
            {
                HandleStorageTypesEngineState(state);
            }
        });
    }

    private void HandleStorageTypesEngineState(DesktopSearchEngineState state)
    {
        var root = _searchEngine.StorageRootPath;
        var sourceAvailable = !_closed &&
            !state.IsBusy &&
            (state.Mode is DesktopSearchMode.Native or DesktopSearchMode.Fallback) &&
            root is not null;

        StorageTypesRefreshButton.IsEnabled =
            _storageViewMode == StorageViewMode.Types && sourceAvailable;
        UpdateStorageTypesNavigationState();

        if (root is null)
        {
            return;
        }

        var sourceKey = CreateStorageSourceKey(state.Mode, root);
        if (string.Equals(sourceKey, _storageTypesSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _storageTypesSourceKey = sourceKey;
        _storageFileTypeAnalysis = null;
        _storageTypesPath = null;
        _storageFileTypes.Clear();
        _storageCategories.Clear();
        ResetStorageTypesPresentation();
        Interlocked.Increment(ref _storageTypeGeneration);

        if (_storageViewMode == StorageViewMode.Types)
        {
            // The original ApplyEngineState only knows about the folder view. Mark
            // its source cache as transitioned so it does not queue an unnecessary
            // folder analysis while Types owns the visible Storage surface.
            _storageSourceKey = sourceKey;
            _storageAnalysis = null;
            _storageCurrentPath = null;
            _storageEntries.Clear();
            ResetStorageSummary();
            Interlocked.Increment(ref _storageGeneration);
        }

        if (_activeSection == AppSection.Storage &&
            _storageViewMode == StorageViewMode.Types &&
            sourceAvailable)
        {
            _ = RunStorageTypesAnalysisAsync(root);
        }
    }

    private async void StorageNavigationWithTypesButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(AppSection.Storage);
        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            SetStorageStatus("Storage analysis will be available when indexing is ready.");
            return;
        }

        await LoadActiveStorageViewAsync(root, forceRefresh: false);
    }

    private async void StorageFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _storageTypeGeneration);
        SetStorageViewMode(StorageViewMode.Folders);

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            SetStorageStatus("Folder analysis will be available when indexing is ready.");
            return;
        }

        await LoadActiveStorageViewAsync(root, forceRefresh: false);
    }

    private async void StorageTypesButton_Click(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _storageGeneration);
        SetStorageViewMode(StorageViewMode.Types);

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            SetStorageStatus("File-type analysis will be available when indexing is ready.");
            return;
        }

        await LoadActiveStorageViewAsync(root, forceRefresh: false);
    }

    private async void StorageTypesRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var root = _searchEngine.StorageRootPath;
        if (root is null)
        {
            SetStorageStatus("File-type analysis is not currently available.");
            return;
        }

        await LoadActiveStorageViewAsync(root, forceRefresh: true);
    }

    private async void StorageTypesUpButton_Click(object sender, RoutedEventArgs e)
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

        await RunStorageTypesAnalysisAsync(parent);
    }

    private async void EnableFastIndexWithStorageTypesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        var sourceBeforeElevation = _storageViewMode == StorageViewMode.Types
            ? _storageTypesSourceKey
            : _storageSourceKey;
        SearchBox.IsEnabled = false;
        StorageRefreshButton.IsEnabled = false;
        StorageTypesRefreshButton.IsEnabled = false;
        EnableFastIndexButton.IsEnabled = false;
        SetSearchStatus(string.Empty);
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Increment(ref _storageGeneration);
        Interlocked.Increment(ref _storageTypeGeneration);

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
            if (string.Equals(currentSourceKey, sourceBeforeElevation, StringComparison.OrdinalIgnoreCase))
            {
                await LoadActiveStorageViewAsync(root, forceRefresh: true);
            }
        }
    }

    private async Task LoadActiveStorageViewAsync(string root, bool forceRefresh)
    {
        var path = ResolveCurrentStoragePath(root);
        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);

        if (_storageViewMode == StorageViewMode.Types)
        {
            if (!string.Equals(sourceKey, _storageTypesSourceKey, StringComparison.OrdinalIgnoreCase))
            {
                _storageTypesSourceKey = sourceKey;
                _storageFileTypeAnalysis = null;
                _storageTypesPath = null;
                _storageFileTypes.Clear();
                _storageCategories.Clear();
                ResetStorageTypesPresentation();
                path = root;
            }

            if (!forceRefresh &&
                _storageFileTypeAnalysis is { } cachedTypes &&
                PathsEqual(cachedTypes.RootPath, path))
            {
                ApplyStorageFileTypeAnalysis(cachedTypes);
                return;
            }

            await RunStorageTypesAnalysisAsync(path);
            return;
        }

        if (!string.Equals(sourceKey, _storageSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _storageSourceKey = sourceKey;
            _storageAnalysis = null;
            _storageEntries.Clear();
            ResetStorageSummary();
            path = root;
        }

        if (!forceRefresh &&
            _storageAnalysis is { } cachedFolders &&
            PathsEqual(cachedFolders.RootPath, path))
        {
            ApplyStorageAnalysis(cachedFolders);
            return;
        }

        await RunStorageAnalysisAsync(path);
    }

    private async Task RunStorageTypesAnalysisAsync(string directoryPath)
    {
        if (_closed)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null)
        {
            SetStorageStatus("File-type analysis is not currently available.");
            return;
        }

        if (!IsPathWithinRoot(directoryPath, root))
        {
            directoryPath = root;
        }

        var generation = Interlocked.Increment(ref _storageTypeGeneration);
        SetStorageStatus($"Analyzing file types under {directoryPath} from the shared index…");
        StorageTypesRefreshButton.IsEnabled = false;
        StorageTypesUpButton.IsEnabled = false;

        try
        {
            await _storageGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed || generation != Volatile.Read(ref _storageTypeGeneration))
                {
                    return;
                }

                var analysis = await _searchEngine.AnalyzeStorageFileTypesAsync(
                    directoryPath,
                    StorageTypeAnalysisLimit);
                if (_closed || generation != Volatile.Read(ref _storageTypeGeneration))
                {
                    return;
                }

                ApplyStorageFileTypeAnalysis(analysis);
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
            if (!_closed && generation == Volatile.Read(ref _storageTypeGeneration))
            {
                SetStorageStatus("Another FileOp session is maintaining this index. Refresh file types after that operation completes.");
            }
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code == IndexingServiceErrorCode.SnapshotRequired)
        {
            if (!_closed && generation == Volatile.Read(ref _storageTypeGeneration))
            {
                SetStorageStatus("This storage snapshot is no longer valid. The indexing engine is refreshing it before file-type analysis can continue.");
            }
        }
        catch (Exception exception)
        {
            if (!_closed && generation == Volatile.Read(ref _storageTypeGeneration))
            {
                SetStorageStatus($"File-type analysis failed: {exception.Message}");
            }
        }
        finally
        {
            if (!_closed && generation == Volatile.Read(ref _storageTypeGeneration))
            {
                StorageTypesRefreshButton.IsEnabled =
                    _storageViewMode == StorageViewMode.Types &&
                    !_searchEngine.State.IsBusy &&
                    _searchEngine.StorageRootPath is not null;
                UpdateStorageTypesNavigationState();
            }
        }
    }

    private void ApplyStorageFileTypeAnalysis(StorageFileTypeAnalysis analysis)
    {
        _storageFileTypeAnalysis = analysis;
        _storageTypesPath = analysis.RootPath;
        _storageCurrentPath = analysis.RootPath;
        StoragePathText.Text = analysis.RootPath;
        StorageLogicalText.Text = ByteFormatter.Format(analysis.LogicalBytes);
        StorageAllocatedText.Text = analysis.AllocatedBytes is { } allocated
            ? ByteFormatter.Format(allocated)
            : "Unknown";
        StorageFilesText.Text = $"{analysis.UniqueFileCount:N0}";
        StorageAliasesText.Text = $"{analysis.HardLinkAliasCount:N0}";

        var usePhysicalWeights = analysis.AllocatedBytes.HasValue;
        _storageFileTypes.Clear();
        foreach (var type in analysis.Types)
        {
            _storageFileTypes.Add(StorageFileTypeRow.FromEntry(type, usePhysicalWeights));
        }

        BuildStorageCategoryRows(analysis, usePhysicalWeights);

        var unit = usePhysicalWeights ? "physical allocation" : "logical size";
        var extensionStatus = analysis.Types.Count == analysis.TypeCount
            ? $"{analysis.TypeCount:N0} extension group(s) shown"
            : $"Top {analysis.Types.Count:N0} of {analysis.TypeCount:N0} extension groups shown";
        StorageCategoryStatusText.Text = $"Exact category totals · {unit} · {extensionStatus}";

        var scope = _searchEngine.StorageCoversWholeVolume
            ? "whole-volume native index"
            : "profile fallback snapshot";
        var aliases = analysis.HardLinkAliasCount > 0
            ? $" · {analysis.HardLinkAliasCount:N0} hard-link alias(es)"
            : string.Empty;
        SetStorageStatus(
            $"{analysis.Types.Count:N0} of {analysis.TypeCount:N0} file types · " +
            $"{analysis.Categories.Count:N0} exact categories · " +
            $"{analysis.UniqueFileCount:N0} unique files{aliases} · {scope}");

        UpdateStorageTypesNavigationState();
    }

    private void BuildStorageCategoryRows(StorageFileTypeAnalysis analysis, bool usePhysicalWeights)
    {
        _storageCategories.Clear();
        var rootWeight = analysis.AllocatedBytes ?? analysis.LogicalBytes;
        foreach (var category in analysis.Categories)
        {
            var weight = usePhysicalWeights
                ? category.AllocatedBytes ?? 0
                : category.LogicalBytes;
            _storageCategories.Add(StorageCategoryRow.Create(
                FormatStorageCategory(category.Category),
                weight,
                rootWeight,
                category.FileCount,
                category.HardLinkAliasCount,
                category.TypeCount));
        }
    }

    private void SetStorageViewMode(StorageViewMode mode)
    {
        _storageViewMode = mode;
        var foldersVisible = mode == StorageViewMode.Folders;
        StorageFolderPanel.Visibility = foldersVisible ? Visibility.Visible : Visibility.Collapsed;
        StorageTypesPanel.Visibility = foldersVisible ? Visibility.Collapsed : Visibility.Visible;
        StorageUpButton.Visibility = foldersVisible ? Visibility.Visible : Visibility.Collapsed;
        StorageRefreshButton.Visibility = foldersVisible ? Visibility.Visible : Visibility.Collapsed;
        StorageTypesUpButton.Visibility = foldersVisible ? Visibility.Collapsed : Visibility.Visible;
        StorageTypesRefreshButton.Visibility = foldersVisible ? Visibility.Collapsed : Visibility.Visible;
        StorageFoldersButton.IsEnabled = !foldersVisible;
        StorageTypesButton.IsEnabled = foldersVisible;

        if (foldersVisible)
        {
            UpdateStorageNavigationState();
            RenderStorageTreemap();
        }
        else
        {
            UpdateStorageTypesNavigationState();
        }
    }

    private void UpdateStorageTypesNavigationState()
    {
        var root = _searchEngine.StorageRootPath;
        StorageTypesUpButton.IsEnabled = !_closed &&
            _storageViewMode == StorageViewMode.Types &&
            !_searchEngine.State.IsBusy &&
            root is not null &&
            _storageCurrentPath is not null &&
            !PathsEqual(root, _storageCurrentPath);
    }

    private string ResolveCurrentStoragePath(string root)
    {
        var path = _storageCurrentPath;
        return string.IsNullOrWhiteSpace(path) || !IsPathWithinRoot(path, root)
            ? root
            : path;
    }

    private void ResetStorageTypesPresentation()
    {
        StorageCategoryStatusText.Text = "Choose Types to analyze extensions and categories from the shared index.";
    }

    private static string FormatStorageCategory(StorageFileCategory category) => category switch
    {
        StorageFileCategory.NoExtension => "No extension",
        StorageFileCategory.DiskImages => "Disk images",
        _ => category.ToString(),
    };

    private enum StorageViewMode
    {
        Folders,
        Types,
    }
}

public sealed record StorageFileTypeRow(
    string Extension,
    string ExtensionText,
    StorageFileCategory Category,
    string CategoryText,
    long LogicalBytes,
    long? AllocatedBytes,
    long ChartBytes,
    int FileCount,
    int HardLinkAliasCount,
    string LogicalText,
    string AllocatedText,
    string FileCountText,
    string AliasCountText)
{
    public static StorageFileTypeRow FromEntry(StorageFileTypeEntry entry, bool usePhysicalWeight) => new(
        entry.Extension,
        string.IsNullOrEmpty(entry.Extension) ? "(no extension)" : $".{entry.Extension}",
        entry.Category,
        FormatCategory(entry.Category),
        entry.LogicalBytes,
        entry.AllocatedBytes,
        usePhysicalWeight ? entry.AllocatedBytes ?? 0 : entry.LogicalBytes,
        entry.FileCount,
        entry.HardLinkAliasCount,
        ByteFormatter.Format(entry.LogicalBytes),
        entry.AllocatedBytes is { } allocated ? ByteFormatter.Format(allocated) : "Unknown",
        $"{entry.FileCount:N0}",
        $"{entry.HardLinkAliasCount:N0}");

    private static string FormatCategory(StorageFileCategory category) => category switch
    {
        StorageFileCategory.NoExtension => "No extension",
        StorageFileCategory.DiskImages => "Disk images",
        _ => category.ToString(),
    };
}

public sealed record StorageCategoryRow(
    string Name,
    long Bytes,
    double Percent,
    string SizeText,
    string DetailText)
{
    public static StorageCategoryRow Create(
        string name,
        long bytes,
        long rootBytes,
        int files,
        int aliases,
        int typeCount)
    {
        var percent = rootBytes > 0
            ? Math.Clamp(bytes * 100d / rootBytes, 0d, 100d)
            : 0d;
        var detail = $"{typeCount:N0} type(s) · {files:N0} file names" +
            (aliases > 0 ? $" · {aliases:N0} hard-link alias(es)" : string.Empty);
        return new StorageCategoryRow(
            name,
            bytes,
            percent,
            ByteFormatter.Format(bytes),
            detail);
    }
}
