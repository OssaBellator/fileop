using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;
using FileOp.Windows.Storage;

namespace FileOp.App;

public sealed partial class MainWindow
{
    private readonly WindowsStorageCleanupReadinessService _storageCleanupReadinessService = new();

    internal async Task CheckKnownLocationCleanupReadinessAsync(string requestedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        if (_closed || !_storageOptimizationInitialized)
        {
            return;
        }

        if (_performanceDiskIoCaptureActive || _storageSameSizeVerificationActive)
        {
            _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                "Cleanup readiness is unavailable while Disk I/O capture or duplicate content verification is active.");
            return;
        }

        if (!await _storageGate.WaitAsync(0))
        {
            _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                "Cleanup readiness is unavailable while another Storage analysis is active.");
            return;
        }

        try
        {
            if (_storageKnownLocationReview is not { } review ||
                _searchEngine.StorageRootPath is not { } activeRoot ||
                _searchEngine.State.IsBusy ||
                _searchEngine.State.Mode != DesktopSearchMode.Native ||
                !_searchEngine.StorageOptimizationAvailable)
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    "Known-location review evidence is no longer current for an active native indexed volume. Refresh Optimize before checking cleanup readiness.");
                return;
            }

            StorageKnownLocationReview? matchedLocation = null;
            StorageReviewCandidate? matchedCandidate = null;
            foreach (var location in review.Locations)
            {
                var candidate = location.Candidates.FirstOrDefault(candidate =>
                    PathsEqual(candidate.Path, requestedPath));
                if (candidate is not null)
                {
                    matchedLocation = location;
                    matchedCandidate = candidate;
                    break;
                }
            }

            if (matchedLocation is null ||
                matchedCandidate is null ||
                matchedLocation.Status != StorageReviewLocationStatus.Available ||
                string.IsNullOrWhiteSpace(matchedLocation.RootPath) ||
                !PathsEqual(review.ActiveVolumeRootPath, activeRoot) ||
                !IsPathWithinRoot(matchedLocation.RootPath, activeRoot) ||
                !IsPathWithinRoot(matchedCandidate.Path, matchedLocation.RootPath))
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    "That path is not an available candidate in the current known-location review for this indexed volume.");
                return;
            }

            _storageOptimizationView.SetKnownLocationCleanupReadinessLoading(matchedCandidate.Name);

            StorageCleanupReadinessPreview preview;
            try
            {
                preview = await _storageCleanupReadinessService.PreviewAsync(
                    matchedCandidate,
                    matchedLocation.RootPath,
                    _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    $"Cleanup readiness could not be checked: {exception.Message}");
                return;
            }

            if (_closed)
            {
                return;
            }

            var currentRoot = _searchEngine.StorageRootPath;
            if (!ReferenceEquals(review, _storageKnownLocationReview) ||
                currentRoot is null ||
                !PathsEqual(currentRoot, activeRoot) ||
                _searchEngine.State.Mode != DesktopSearchMode.Native ||
                _searchEngine.State.IsBusy ||
                !_searchEngine.StorageOptimizationAvailable)
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    "The indexed source or review changed while cleanup readiness was being checked. Refresh Optimize before using the result.");
                return;
            }

            _storageOptimizationView.ApplyKnownLocationCleanupReadiness(preview);
        }
        finally
        {
            _storageGate.Release();
        }
    }
}
