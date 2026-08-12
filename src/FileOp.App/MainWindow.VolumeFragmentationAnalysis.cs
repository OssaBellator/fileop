namespace FileOp.App;

public sealed partial class MainWindow
{
    internal async Task CapturePerformanceVolumeFragmentationAnalysisAsync()
    {
        if (_closed ||
            _storageViewMode != StorageViewMode.Optimize ||
            _storageSameSizeVerificationActive ||
            _performanceDiskIoCaptureActive)
        {
            return;
        }

        var volumeRoot = _searchEngine.VolumeFragmentationAnalysisRoot;
        if (volumeRoot is null)
        {
            _storageOptimizationView.SetVolumeFragmentationAnalysisUnavailable(
                "Fragmentation compatibility analysis requires a current Storage source that resolves to one explicit local drive-letter root. No WMI analysis was attempted.");
            return;
        }

        // Reuse the existing explicit performance-capture exclusion flag. The
        // analysis can be substantially longer than the two-second DiskIo or
        // one-second machine samples, so no other explicit diagnostics or
        // same-size content hashing may overlap it.
        _performanceDiskIoCaptureActive = true;
        _storageOptimizationView.SetReadyForRefresh(false);
        _storageOptimizationView.SetDiskIoReadyForCapture(false);
        _storageOptimizationView.SetMachineProcessActivityReadyForCapture(false);
        _storageOptimizationView.SetVolumeFragmentationAnalysisLoading(volumeRoot);
        try
        {
            var result = await _searchEngine.CaptureVolumeFragmentationAnalysisAsync(volumeRoot);
            if (_closed)
            {
                return;
            }

            var currentVolumeRoot = _searchEngine.VolumeFragmentationAnalysisRoot;
            if (!string.Equals(
                    currentVolumeRoot,
                    volumeRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                _storageOptimizationView.SetVolumeFragmentationAnalysisUnavailable(
                    $"Fragmentation analysis completed for {volumeRoot}, but the current Storage source is now on {currentVolumeRoot ?? "an unavailable/non-drive source"}. The stale result was discarded.");
                return;
            }

            _storageOptimizationView.ApplyVolumeFragmentationAnalysis(result);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                _storageOptimizationView.SetVolumeFragmentationAnalysisUnavailable(
                    $"Volume fragmentation analysis failed: {exception.Message}");
            }
        }
        finally
        {
            _performanceDiskIoCaptureActive = false;
            if (!_closed)
            {
                var optimizeVisible = _storageViewMode == StorageViewMode.Optimize;
                var supplementaryReady =
                    optimizeVisible && !_storageSameSizeVerificationActive;
                _storageOptimizationView.SetMachineProcessActivityReadyForCapture(
                    supplementaryReady);
                _storageOptimizationView.SetDiskIoReadyForCapture(
                    supplementaryReady);
                _storageOptimizationView.SetVolumeFragmentationAnalysisReadyForCapture(
                    supplementaryReady &&
                    _searchEngine.VolumeFragmentationAnalysisRoot is not null);
                _storageOptimizationView.SetReadyForRefresh(
                    optimizeVisible &&
                    _searchEngine.StorageOptimizationAvailable &&
                    !_searchEngine.State.IsBusy &&
                    !_storageSameSizeVerificationActive);
            }
        }
    }
}
