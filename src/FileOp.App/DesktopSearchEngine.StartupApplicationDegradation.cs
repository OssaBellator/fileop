using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly IStartupApplicationDegradationProvider _startupApplicationDegradationProvider =
        new WindowsStartupApplicationDegradationProvider();

    public Task<StartupApplicationDegradationResult> CaptureStartupApplicationDegradationAsync()
    {
        ThrowIfDisposed();
        var cancellationToken = _lifetimeCancellation.Token;
        return Task.Run(
            () => _startupApplicationDegradationProvider.Query(
                StartupApplicationDegradationBudget.Default,
                cancellationToken),
            cancellationToken);
    }
}
