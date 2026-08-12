using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class StorageOptimizationView
{
    public void SetVolumeFragmentationAnalysisLoading(string volumeRoot)
    {
        PerformanceDiagnostics.SetVolumeFragmentationAnalysisLoading(volumeRoot);
        PerformanceDiagnostics.SetMachineProcessActivityReadyForCapture(false);
        RefreshButton.IsEnabled = false;
        _sameSizeVerificationControlsBlocked = true;
        RefreshSameSizeRows();
    }

    public void SetVolumeFragmentationAnalysisReadyForCapture(bool ready) =>
        PerformanceDiagnostics.SetVolumeFragmentationAnalysisReadyForCapture(ready);

    public void SetVolumeFragmentationAnalysisUnavailable(
        string message,
        bool canRetry = true)
    {
        PerformanceDiagnostics.SetVolumeFragmentationAnalysisUnavailable(message, canRetry);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }

    public void ApplyVolumeFragmentationAnalysis(
        VolumeFragmentationAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        PerformanceDiagnostics.ApplyVolumeFragmentationAnalysis(result);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }
}
