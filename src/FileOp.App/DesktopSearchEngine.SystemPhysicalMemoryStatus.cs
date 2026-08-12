using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly ISystemPhysicalMemoryStatusProvider _systemPhysicalMemoryStatusProvider =
        new WindowsSystemPhysicalMemoryStatusProvider();

    public SystemPhysicalMemoryStatusResult CaptureSystemPhysicalMemoryStatus()
    {
        ThrowIfDisposed();
        return _systemPhysicalMemoryStatusProvider.Query();
    }
}
