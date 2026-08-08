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
        _searchEngine.StateChanged += StorageTypesEngine_StateChanged;
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
            state.Mode is DesktopSearchMode.Native or DesktopSearchMode.Fallback &&
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

        if (_activeSection == AppSection.Storage &&
            _storageViewMode == StorageViewMode.Types &&
            sourceAvailable)
        {
            _ = RunStorageTypesAnalysisAsync(root);
        }
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

        var path = ResolveCurrentStoragePath(root);
        if (_storageAnalysis is { } cached && PathsEqual(cached.RootPath, path))
        {
            ApplyStorageAnalysis(cached);
            return;
        }

        await RunStorageAnalysisAsync(path);
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

        var path = ResolveCurrentStoragePath(root);
        if (_storageFileTypeAnalysis is { } cached && PathsEqual(cached.RootPath, path))
        {
            ApplyStorageFileTypeAnalysis(cached);
            return;
        }

        await RunStorageTypesAnalysisAsync(path);
    }

    private async void StorageTypesRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var root = _searchEngine.StorageRootPath;
        if (root is null)
        {
            SetStorageStatus("File-type analysis is not currently available.");
            return;
        }

        await RunStorageTypesAnalysisAsync(ResolveCurrentStoragePath(root));
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

        var completeTypes = analysis.Types.Count == analysis.TypeCount;
        var unit = usePhysicalWeights ? "physical allocation" : "logical size";
        StorageCategoryStatusText.Text = completeTypes
            ? $"Exact category totals · {unit}"
            : $"Top {analysis.Types.Count:N0} of {analysis.TypeCount:N0} types · omitted remainder is left unclassified · {unit}";

        var scope = _searchEngine.StorageCoversWholeVolume
            ? "whole-volume native index"
            : "profile fallback snapshot";
        var aliases = analysis.HardLinkAliasCount > 0
            ? $" · {analysis.HardLinkAliasCount:N0} hard-link alias(es)"
            : string.Empty;
        SetStorageStatus(
            $"{analysis.Types.Count:N0} of {analysis.TypeCount:N0} file types · " +
            $"{analysis.UniqueFileCount:N0} unique files{aliases} · {scope}");

        UpdateStorageTypesNavigationState();
    }

    private void BuildStorageCategoryRows(StorageFileTypeAnalysis analysis, bool usePhysicalWeights)
    {
        _storageCategories.Clear();
        var rootWeight = analysis.AllocatedBytes ?? analysis.LogicalBytes;
        var groups = analysis.Types
            .GroupBy(static type => type.Category)
            .Select(group => new
            {
                Category = group.Key,
                Weight = group.Sum(type => usePhysicalWeights ? type.AllocatedBytes ?? 0 : type.LogicalBytes),
                Files = group.Sum(static type => type.FileCount),
                Aliases = group.Sum(static type => type.HardLinkAliasCount),
                TypeCount = group.Count(),
            })
            .OrderByDescending(static group => group.Weight)
            .ThenBy(static group => group.Category)
            .ToArray();

        foreach (var group in groups)
        {
            _storageCategories.Add(StorageCategoryRow.Create(
                FormatStorageCategory(group.Category),
                group.Weight,
                rootWeight,
                group.Files,
                group.Aliases,
                group.TypeCount,
                isRemainder: false));
        }

        var representedWeight = groups.Sum(static group => group.Weight);
        var omittedTypeCount = Math.Max(0, analysis.TypeCount - analysis.Types.Count);
        var omittedWeight = Math.Max(0, rootWeight - representedWeight);
        if (omittedTypeCount > 0 && omittedWeight > 0)
        {
            _storageCategories.Add(StorageCategoryRow.Create(
                $"Other {omittedTypeCount:N0} types (not returned)",
                omittedWeight,
                rootWeight,
                files: 0,
                aliases: 0,
                typeCount: omittedTypeCount,
                isRemainder: true));
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
    string DetailText,
    bool IsRemainder)
{
    public static StorageCategoryRow Create(
        string name,
        long bytes,
        long rootBytes,
        int files,
        int aliases,
        int typeCount,
        bool isRemainder)
    {
        var percent = rootBytes > 0
            ? Math.Clamp(bytes * 100d / rootBytes, 0d, 100d)
            : 0d;
        var detail = isRemainder
            ? $"{typeCount:N0} extension groups omitted by the bounded response"
            : $"{typeCount:N0} type(s) · {files:N0} file names" +
              (aliases > 0 ? $" · {aliases:N0} hard-link alias(es)" : string.Empty);
        return new StorageCategoryRow(
            name,
            bytes,
            percent,
            ByteFormatter.Format(bytes),
            detail,
            isRemainder);
    }
}
