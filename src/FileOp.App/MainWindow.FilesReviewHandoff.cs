using Microsoft.UI.Xaml;

namespace FileOp.App;

public sealed partial class MainWindow
{
    internal async Task ReviewPathInFilesAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_closed || !_filesInitialized)
        {
            return;
        }

        var root = _searchEngine.StorageRootPath;
        if (root is null || _searchEngine.State.IsBusy)
        {
            SetFilesStatus("Review handoff is unavailable while indexed Files browsing is not ready.");
            return;
        }

        string fullPath;
        string parentPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            parentPath = Path.GetDirectoryName(fullPath) ?? root;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetFilesStatus("Review handoff could not use the candidate path because it is invalid.");
            return;
        }

        if (!IsPathWithinRoot(fullPath, root) || !IsPathWithinRoot(parentPath, root))
        {
            SetFilesStatus("Review handoff could not open the candidate because it is outside the current indexed Files source.");
            return;
        }

        _filesVisible = true;
        _activeSection = AppSection.Search;
        _searchTimer.Stop();
        Interlocked.Increment(ref _searchGeneration);
        Interlocked.Increment(ref _storageGeneration);
        Interlocked.Increment(ref _storageTypeGeneration);
        Interlocked.Increment(ref _storageHistoryGeneration);
        Interlocked.Increment(ref _storageOptimizationGeneration);
        SetStorageViewMode(StorageViewMode.Folders);

        SearchView.Visibility = Visibility.Collapsed;
        StorageView.Visibility = Visibility.Collapsed;
        _filesView.Visibility = Visibility.Visible;

        var sourceKey = CreateStorageSourceKey(_searchEngine.State.Mode, root);
        if (!string.Equals(sourceKey, _filesSourceKey, StringComparison.OrdinalIgnoreCase))
        {
            _filesSourceKey = sourceKey;
            ResetAllFilesTabs(clearPath: true);
            InvalidateFilesPane(_leftFilesPane);
            InvalidateFilesPane(_rightFilesPane);
        }

        var tab = CreateFilesTab(parentPath);
        _leftFilesPane.Tabs.Add(tab);
        _leftFilesPane.ActiveTabId = tab.Id;
        InvalidateFilesPane(_leftFilesPane);
        ApplyFilesTabs(_leftFilesPane);

        await LoadFilesDirectoryAsync(_leftFilesPane, parentPath, forceRefresh: true);
        if (_closed || !_filesVisible || _leftFilesPane.ActiveTabId != tab.Id)
        {
            return;
        }

        var selected = _filesView.LeftPane.TrySelectVisiblePath(fullPath);
        SetFilesStatus(
            selected
                ? $"Opened a new Files tab for review and selected {Path.GetFileName(fullPath)}. No operation was prepared or queued."
                : $"Opened a new Files tab for {parentPath}. The review candidate is not in the first loaded page; use Load more to locate it. No operation was prepared or queued.");
    }
}
