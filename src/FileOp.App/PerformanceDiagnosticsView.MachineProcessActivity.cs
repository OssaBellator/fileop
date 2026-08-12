using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class PerformanceDiagnosticsView
{
    public void SetMachineProcessActivityLoading() =>
        MachineProcessActivity.SetLoading();

    public void SetMachineProcessActivityReadyForCapture(bool ready) =>
        MachineProcessActivity.SetReady(ready);

    public void SetMachineProcessActivityUnavailable(string message) =>
        MachineProcessActivity.SetUnavailable(message);

    public void ApplyMachineProcessActivity(MachineProcessActivityResult result) =>
        MachineProcessActivity.Apply(result);
}
