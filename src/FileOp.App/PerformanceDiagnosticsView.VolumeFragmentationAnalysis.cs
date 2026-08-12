using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class PerformanceDiagnosticsView
{
    public void SetVolumeFragmentationAnalysisLoading(string volumeRoot) =>
        VolumeFragmentationAnalysis.SetLoading(volumeRoot);

    public void SetVolumeFragmentationAnalysisReadyForCapture(bool ready) =>
        VolumeFragmentationAnalysis.SetReady(ready);

    public void SetVolumeFragmentationAnalysisUnavailable(
        string message,
        bool canRetry = true) =>
        VolumeFragmentationAnalysis.SetUnavailable(message, canRetry);

    public void ApplyVolumeFragmentationAnalysis(
        VolumeFragmentationAnalysisResult result) =>
        VolumeFragmentationAnalysis.Apply(result);
}
