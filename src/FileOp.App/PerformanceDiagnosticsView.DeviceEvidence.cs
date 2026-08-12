using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class PerformanceDiagnosticsView
{
    public void SetDiskIoDeviceEvidenceLoading() =>
        _diskIoAttributionView.SetDeviceEvidenceLoading();

    public void SetDiskIoDeviceEvidenceUnavailable(string message) =>
        _diskIoAttributionView.SetDeviceEvidenceUnavailable(message);

    public void ResetDiskIoDeviceEvidence() =>
        _diskIoAttributionView.ResetDeviceEvidence();

    public void ApplyDiskIoDeviceEvidence(
        DiskIoDeviceEvidenceSnapshot evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var enriched = AttachDiskIoFailurePrediction(evidence);
        _diskIoAttributionView.ApplyDeviceEvidence(
            enriched.Rows,
            enriched.QueryElapsed);
        _diskIoAttributionView.ApplyFailurePredictionEvidence(enriched.Rows);
    }
}
