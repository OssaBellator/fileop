using FileOp.Windows.IndexingService;
using Microsoft.UI.Xaml;

namespace FileOp.App;

public sealed partial class MainWindow
{
    internal async Task ReviewPathInFilesAsync(string requestedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        if (_closed || !_filesInitialized)
        {
            return;
        }

        if (_performanceDiskIoCaptureActive || _storageSameSizeVerificationActive)
        {
            SetStorageStatus(
                "Review in Files is unavailable while Disk I/O capture or duplicate content verification is active.");
            return;
        }

        if (_storageGate.CurrentCount == 0)
        {
            SetStorageStatus(
                "Review in Files is waiting for the current Storage analysis to finish.");
            return;
        }

        var review = _storageKnownLocationReview;
        var root = _searchEngine.StorageRootPath;
        if (review is null ||
            root is null ||
            _searchEngine.State.IsBusy ||
            _searchEngine.State.Mode != DesktopSearchMode.Native ||
            !_searchEngine.StorageOptimizationAvailable)
        {
            SetStorageStatus(
                "Known-location review evidence is no longer current for an active native indexed volume. Refresh Optimize before reviewing it in Files.");
            return;
        }

        if (!PathsEqual(root, review.ActiveVolumeRootPath))
        {
            SetStorageStatus(
                "The active indexed volume changed after this known-location review. Refresh Optimize before reviewing it in Files.");
            return;
        }

        var candidate = review.Locations
            .SelectMany(static location => location.Candidates)
            .FirstOrDefault(candidate => PathsEqual(candidate.Path, requestedPath));
        if (candidate is null || !IsPathWithinRoot(candidate.Path, root))
        {
            SetStorageStatus(
                "That path is not a candidate in the current known-location review for this indexed volume.");
            return;
        }

        string parentPath;
        try
        {
            parentPath = Path.GetDirectoryName(candidate.Path) ?? string.Empty;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetStorageStatus("The review candidate path is invalid and cannot be handed to Files.");
            return;
        }

        if (string.IsNullOrWhiteSpace(parentPath) || !IsPathWithinRoot(parentPath, root))
        {
            SetStorageStatus(
                "The review candidate parent is outside the current indexed volume and cannot be handed to Files.");
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

        var paneView = _filesView.LeftPane;
        if (!paneView.IsDirectoryReady ||
            paneView.CurrentPath is null ||
            !PathsEqual(paneView.CurrentPath, parentPath))
        {
            SetFilesStatus(
                "The review candidate parent directory could not be loaded from the current indexed source.");
            return;
        }

        var selected = paneView.SetReviewSelectionHint(candidate.Path);
        paneView.SetStatus(
            selected
                ? $"Selected review candidate {candidate.Name}. Known-location evidence does not authorize deletion."
                : $"Opened the indexed parent for {candidate.Name}. The candidate is not in the currently loaded page; Load more can reveal and select it if it is still present. The review evidence may be stale and does not authorize deletion.",
            parentPath);
        SetFilesStatus(
            selected
                ? "Known-location candidate handed to Files for non-destructive inspection."
                : "Known-location parent opened in Files; the candidate is not in the currently loaded exact page.");
    }
}
