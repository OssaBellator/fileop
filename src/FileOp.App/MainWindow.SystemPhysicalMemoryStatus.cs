using FileOp.Core.Performance;

namespace FileOp.App;

public sealed partial class MainWindow
{
    internal Task<SystemPhysicalMemoryStatusResult> CapturePerformanceSystemPhysicalMemoryStatusAsync()
    {
        if (_closed)
        {
            return Task.FromResult(
                SystemPhysicalMemoryStatusResult.Unavailable(
                    SystemPhysicalMemoryStatusAvailability.Unavailable,
                    TimeSpan.Zero,
                    "The FileOp window is closing; physical-memory evidence was not queried."));
        }

        var cancellationToken = _lifetimeCancellation.Token;
        return Task.Run(
            _searchEngine.CaptureSystemPhysicalMemoryStatus,
            cancellationToken);
    }
}
