using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly IDiskIoAttributionProvider _diskIoAttributionProvider =
        new WindowsDiskIoAttributionProvider();

    public ValueTask<DiskIoCaptureResult> CaptureDiskIoAttributionAsync()
    {
        ThrowIfDisposed();
        return _diskIoAttributionProvider.CaptureAsync(
            DiskIoCaptureBudget.Default,
            _lifetimeCancellation.Token);
    }
}
