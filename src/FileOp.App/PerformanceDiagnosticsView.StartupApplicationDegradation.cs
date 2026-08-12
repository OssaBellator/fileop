using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class PerformanceDiagnosticsView
{
    public void SetStartupApplicationDegradationLoading() =>
        StartupApplicationDegradation.SetLoading();

    public void SetStartupApplicationDegradationReadyForCapture(bool ready) =>
        StartupApplicationDegradation.SetReady(ready);

    public void SetStartupApplicationDegradationUnavailable(
        string message,
        bool canRetry = true) =>
        StartupApplicationDegradation.SetUnavailable(message, canRetry);

    public void ApplyStartupApplicationDegradation(
        StartupApplicationDegradationResult result) =>
        StartupApplicationDegradation.Apply(result);
}
