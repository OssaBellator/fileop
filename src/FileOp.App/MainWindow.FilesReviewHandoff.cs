using FileOp.Core.Storage;
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

        if (!await _storageGate.WaitAsync(0))
        {
            SetStorageStatus(
                "Review in Files is waiting for the current Storage analysis to finish.");
            return;
        }

        StorageKnownLocationReviewSnapshot review;
        string root;
        string candidatePath;
        string candidateName;
        string parentPath;
        Guid tabId;
        try
        {
            if (_storageKnownLocationReview is not { } currentReview ||
                _searchEngine.StorageRootPath is not { } currentRoot ||
                _searchEngine.State.IsBusy ||
                _searchEngine.State.Mode != DesktopSearchMode.Native ||
                !_searchEngine.StorageOptimizationAvailable)
            {
                SetStorageStatus(
                    "Known-location review evidence is no longer current for an active native indexed volume. Refresh Optimize before reviewing it in Files.");
                return;
            }

            review = currentReview;
            root = currentRoot;
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

            candidatePath = candidate.Path;
            candidateName = candidate.Name;

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

            var tab = _leftFilesPane.Tabs.FirstOrDefault(existing =>
                existing.CurrentPath is not null && PathsEqual(existing.CurrentPath, parentPath));
            if (tab is null)
            {
                tab = CreateFilesTab(parentPath);
                _leftFilesPane.Tabs.Add(tab);
            }
            _leftFilesPane.ActiveTabId = tab.Id;
            tabId = tab.Id;
            InvalidateFilesPane(_leftFilesPane);
            ApplyFilesTabs(_leftFilesPane);
        }
        finally
        {
            // Files paging takes this same gate itself. Release before awaiting the
            // existing loader so the handoff serializes instead of deadlocking.
            _storageGate.Release();
        }

        await LoadFilesDirectoryAsync(_leftFilesPane, parentPath, forceRefresh: true);
        if (_closed || !_filesVisible || _leftFilesPane.ActiveTabId != tabId)
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

        var currentRootAfterLoad = _searchEngine.StorageRootPath;
        if (!ReferenceEquals(review, _storageKnownLocationReview) ||
            currentRootAfterLoad is null ||
            !PathsEqual(currentRootAfterLoad, root) ||
            _searchEngine.State.Mode != DesktopSearchMode.Native ||
            _searchEngine.State.IsBusy ||
            !_searchEngine.StorageOptimizationAvailable)
        {
            paneView.SetStatus(
                "The indexed source changed while the review candidate was being handed to Files. The parent directory is open, but FileOp did not auto-select stale review evidence.",
                parentPath);
            SetFilesStatus(
                "Known-location handoff stopped after the indexed source changed; refresh Optimize before selecting that review candidate.");
            return;
        }

        var selected = paneView.SetReviewSelectionHint(candidatePath);
        paneView.SetStatus(
            selected
                ? $"Selected review candidate {candidateName}. Known-location evidence does not authorize deletion."
                : $"Opened the indexed parent for {candidateName}. The candidate is not in the currently loaded page; Load more can reveal and select it if it is still present. The review evidence may be stale and does not authorize deletion.",
            parentPath);
        SetFilesStatus(
            selected
                ? "Known-location candidate handed to Files for non-destructive inspection."
                : "Known-location parent opened in Files; the candidate is not in the currently loaded exact page.");
    }
}
