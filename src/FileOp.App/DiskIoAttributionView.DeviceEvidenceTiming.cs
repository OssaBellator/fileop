using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class DiskIoAttributionView
{
    public void ApplyDeviceEvidence(
        IReadOnlyList<DiskIoPhysicalDiskDeviceEvidence> evidence,
        TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        ApplyDeviceEvidence(evidence);
        _deviceEvidenceStatusText!.Text +=
            $" Post-capture device-query elapsed: {elapsed.TotalMilliseconds:N2} ms.";
    }
}
