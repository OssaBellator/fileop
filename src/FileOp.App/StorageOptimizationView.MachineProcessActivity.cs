using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class StorageOptimizationView
{
    public void SetMachineProcessActivityLoading()
    {
        PerformanceDiagnostics.SetMachineProcessActivityLoading();
        RefreshButton.IsEnabled = false;
        _sameSizeVerificationControlsBlocked = true;
        RefreshSameSizeRows();
    }

    public void SetMachineProcessActivityReadyForCapture(bool ready) =>
        PerformanceDiagnostics.SetMachineProcessActivityReadyForCapture(ready);

    public void SetMachineProcessActivityUnavailable(string message)
    {
        PerformanceDiagnostics.SetMachineProcessActivityUnavailable(message);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }

    public void ApplyMachineProcessActivity(
        MachineProcessActivityResult result,
        SystemCpuActivityResult systemCpuActivity)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(systemCpuActivity);
        PerformanceDiagnostics.ApplyMachineProcessActivity(
            result,
            systemCpuActivity);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }
}
