using FileOp.Core.Storage;
using FileOp.Windows.Storage;

namespace FileOp.App;

public sealed partial class MainWindow
{
    private readonly WindowsStorageCleanupReadinessService _storageCleanupReadinessService = new();
    private bool _storageCleanupReadinessActive;

    internal async Task CheckKnownLocationCleanupReadinessAsync(
        string requestedPath,
        string requestedReviewRoot,
        StorageReviewProvenance requestedProvenance,
        string requestedRuleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedReviewRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedRuleId);
        if (_closed || !_storageOptimizationInitialized || _storageCleanupReadinessActive)
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

        _storageCleanupReadinessActive = true;
        try
        {
            if (_storageKnownLocationReview is not { } review ||
                _searchEngine.StorageRootPath is not { } activeRoot ||
                _searchEngine.StorageVolumeIdentity is not { } activeVolumeIdentity ||
                _searchEngine.State.IsBusy ||
                _searchEngine.State.Mode != DesktopSearchMode.Native ||
                !_searchEngine.StorageOptimizationAvailable)
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    "Known-location review evidence is no longer current for an active native indexed source. Refresh Optimize before checking cleanup readiness.");
                return;
            }

            StorageKnownLocationReview? matchedLocation = null;
            StorageReviewCandidate? matchedCandidate = null;
            var matchCount = 0;
            foreach (var location in review.Locations)
            {
                if (location.Provenance != requestedProvenance ||
                    !PathsEqual(location.RootPath, requestedReviewRoot))
                {
                    continue;
                }

                foreach (var candidate in location.Candidates)
                {
                    if (!PathsEqual(candidate.Path, requestedPath) ||
                        candidate.Provenance != requestedProvenance ||
                        !string.Equals(candidate.RuleId, requestedRuleId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matchCount++;
                    matchedLocation = location;
                    matchedCandidate = candidate;
                }
            }

            if (matchCount != 1 ||
                matchedLocation is null ||
                matchedCandidate is null ||
                matchedLocation.Status != StorageReviewLocationStatus.Available ||
                string.IsNullOrWhiteSpace(matchedLocation.RootPath) ||
                review.ActiveVolumeIdentity != activeVolumeIdentity ||
                !PathsEqual(review.ActiveVolumeRootPath, activeRoot) ||
                !IsPathWithinRoot(matchedCandidate.Path, matchedLocation.RootPath))
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    "That exact path/root/rule candidate is not uniquely available in the current known-location review. Refresh Optimize before checking this read-only evidence.");
                return;
            }

            try
            {
                if (!await _searchEngine.IsNativeReviewSourceCurrentAsync(
                        activeVolumeIdentity,
                        activeRoot,
                        _lifetimeCancellation.Token))
                {
                    _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                        "The native volume catalog no longer matches this known-location review. Refresh Optimize before checking cleanup readiness.");
                    return;
                }
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    $"Cleanup readiness could not confirm the current indexed source: {exception.Message}");
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
            var currentVolumeIdentity = _searchEngine.StorageVolumeIdentity;
            if (!ReferenceEquals(review, _storageKnownLocationReview) ||
                currentRoot is null ||
                currentVolumeIdentity != activeVolumeIdentity ||
                !PathsEqual(currentRoot, activeRoot) ||
                _searchEngine.State.Mode != DesktopSearchMode.Native ||
                _searchEngine.State.IsBusy ||
                !_searchEngine.StorageOptimizationAvailable)
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    "The active indexed source or review changed while cleanup readiness was being checked. Refresh Optimize before using the result.");
                return;
            }

            try
            {
                if (!await _searchEngine.IsNativeReviewSourceCurrentAsync(
                        activeVolumeIdentity,
                        activeRoot,
                        _lifetimeCancellation.Token))
                {
                    _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                        "The native volume catalog changed while cleanup readiness was being checked. Refresh Optimize before using the result.");
                    return;
                }
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _storageOptimizationView.SetKnownLocationCleanupReadinessUnavailable(
                    $"Cleanup readiness could not re-confirm the indexed source: {exception.Message}");
                return;
            }

            _storageOptimizationView.ApplyKnownLocationCleanupReadiness(preview);
        }
        finally
        {
            _storageCleanupReadinessActive = false;
            _storageGate.Release();
        }
    }
}
