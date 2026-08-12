using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.App;

public sealed partial class PerformanceDiagnosticsView
{
    private readonly IPhysicalDiskFailurePredictionProvider _physicalDiskFailurePredictionProvider =
        new WindowsPhysicalDiskFailurePredictionProvider();

    private DiskIoDeviceEvidenceSnapshot AttachDiskIoFailurePrediction(
        DiskIoDeviceEvidenceSnapshot evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return DiskIoDeviceEvidenceCollector.AttachFailurePrediction(
            evidence,
            _physicalDiskFailurePredictionProvider);
    }
}
