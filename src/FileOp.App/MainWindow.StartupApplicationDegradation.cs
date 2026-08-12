namespace FileOp.App;

public sealed partial class MainWindow
{
    internal async Task CapturePerformanceStartupApplicationDegradationAsync()
    {
        if (_closed || _storageViewMode != StorageViewMode.Optimize)
        {
            return;
        }
        if (_storageSameSizeVerificationActive || _performanceDiskIoCaptureActive)
        {
            _storageOptimizationView.SetStartupApplicationDegradationBusy(
                "Another explicit Performance/Storage diagnostic is already active. Startup degradation history was not read.");
            return;
        }

        _performanceDiskIoCaptureActive = true;
        _storageOptimizationView.SetReadyForRefresh(false);
        _storageOptimizationView.SetDiskIoReadyForCapture(false);
        _storageOptimizationView.SetMachineProcessActivityReadyForCapture(false);
        _storageOptimizationView.SetVolumeFragmentationAnalysisReadyForCapture(false);
        _storageOptimizationView.SetStartupApplicationDegradationLoading();
        try
        {
            var result = await _searchEngine.CaptureStartupApplicationDegradationAsync();
            if (_closed)
            {
                return;
            }

            _storageOptimizationView.ApplyStartupApplicationDegradation(result);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                _storageOptimizationView.SetStartupApplicationDegradationUnavailable(
                    $"Startup degradation compatibility history failed: {exception.Message}");
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
                _storageOptimizationView.SetStartupApplicationDegradationReadyForCapture(
                    supplementaryReady);
                _storageOptimizationView.SetReadyForRefresh(
                    optimizeVisible &&
                    _searchEngine.StorageOptimizationAvailable &&
                    !_searchEngine.State.IsBusy &&
                    !_storageSameSizeVerificationActive);
            }
        }
    }
}
