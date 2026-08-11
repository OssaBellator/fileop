using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly IDiskIoAttributionProvider _diskIoAttributionProvider =
        new WindowsDiskIoAttributionProvider();
    private readonly IPhysicalDiskDeviceContextProvider _physicalDiskDeviceContextProvider =
        new WindowsPhysicalDiskDeviceContextProvider();
    private readonly INvmeHealthEvidenceProvider _nvmeHealthEvidenceProvider =
        new WindowsNvmeHealthEvidenceProvider();

    public ValueTask<DiskIoCaptureResult> CaptureDiskIoAttributionAsync()
    {
        ThrowIfDisposed();
        return _diskIoAttributionProvider.CaptureAsync(
            DiskIoCaptureBudget.Default,
            _lifetimeCancellation.Token);
    }

    public async ValueTask<IReadOnlyList<DiskIoPhysicalDiskDeviceEvidence>>
        QueryDiskIoDeviceEvidenceAsync(DiskIoCaptureResult result)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != DiskIoCaptureStatus.Completed || result.Report is null)
        {
            return Array.Empty<DiskIoPhysicalDiskDeviceEvidence>();
        }

        var physicalDiskNumbers = result.Report.Disks
            .Select(static disk => disk.PhysicalDiskNumber)
            .ToArray();
        if (physicalDiskNumbers.Length == 0)
        {
            return Array.Empty<DiskIoPhysicalDiskDeviceEvidence>();
        }

        return await Task.Run(
            () => DiskIoDeviceEvidenceCollector.Query(
                physicalDiskNumbers,
                _physicalDiskDeviceContextProvider,
                _nvmeHealthEvidenceProvider),
            _lifetimeCancellation.Token).ConfigureAwait(false);
    }
}
