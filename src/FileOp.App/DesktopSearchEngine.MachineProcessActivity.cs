using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly IMachineProcessActivityProvider _machineProcessActivityProvider =
        new WindowsMachineProcessActivityProvider();

    public ValueTask<MachineProcessActivityResult> CaptureMachineProcessActivityAsync()
    {
        ThrowIfDisposed();
        return _machineProcessActivityProvider.CaptureAsync(
            MachineProcessActivityBudget.Default,
            _lifetimeCancellation.Token);
    }
}
