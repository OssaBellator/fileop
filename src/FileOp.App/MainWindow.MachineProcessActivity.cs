namespace FileOp.App;

public sealed partial class MainWindow
{
    internal async Task CapturePerformanceMachineProcessActivityAsync()
    {
        if (_closed ||
            _storageViewMode != StorageViewMode.Optimize ||
            _storageSameSizeVerificationActive ||
            _performanceDiskIoCaptureActive)
        {
            return;
        }

        // Reuse the existing explicit performance-capture exclusion flag so the
        // machine sample cannot overlap DiskIo, fragmentation analysis, same-size
        // hashing, or a state-handler path that would otherwise re-enable those
        // controls while this sample runs.
        _performanceDiskIoCaptureActive = true;
        _storageOptimizationView.SetReadyForRefresh(false);
        _storageOptimizationView.SetDiskIoReadyForCapture(false);
        _storageOptimizationView.SetVolumeFragmentationAnalysisReadyForCapture(false);
        _storageOptimizationView.SetMachineProcessActivityLoading();
        try
        {
            var result = await _searchEngine.CaptureMachineProcessActivityAsync();
            if (_closed)
            {
                return;
            }

            _storageOptimizationView.ApplyMachineProcessActivity(result);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                _storageOptimizationView.SetMachineProcessActivityUnavailable(
                    $"Machine process activity capture failed: {exception.Message}");
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
