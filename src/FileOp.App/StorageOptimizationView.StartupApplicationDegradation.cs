using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class StorageOptimizationView
{
    public void SetStartupApplicationDegradationLoading()
    {
        PerformanceDiagnostics.SetStartupApplicationDegradationLoading();
        PerformanceDiagnostics.SetMachineProcessActivityReadyForCapture(false);
        PerformanceDiagnostics.SetVolumeFragmentationAnalysisReadyForCapture(false);
        RefreshButton.IsEnabled = false;
        _sameSizeVerificationControlsBlocked = true;
        RefreshSameSizeRows();
    }

    public void SetStartupApplicationDegradationReadyForCapture(bool ready) =>
        PerformanceDiagnostics.SetStartupApplicationDegradationReadyForCapture(ready);

    public void SetStartupApplicationDegradationUnavailable(
        string message,
        bool canRetry = true)
    {
        PerformanceDiagnostics.SetStartupApplicationDegradationUnavailable(message, canRetry);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }

    public void ApplyStartupApplicationDegradation(
        StartupApplicationDegradationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        PerformanceDiagnostics.ApplyStartupApplicationDegradation(result);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }
}
