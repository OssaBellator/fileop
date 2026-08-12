using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly IMachineProcessActivityProvider _machineProcessActivityProvider =
        new WindowsMachineProcessActivityProvider();
    private readonly ISystemCpuActivityProvider _systemCpuActivityProvider =
        new WindowsSystemCpuActivityProvider();

    public async ValueTask<MachineProcessActivityCaptureBundle>
        CaptureMachineProcessActivityAsync()
    {
        ThrowIfDisposed();
        var cancellationToken = _lifetimeCancellation.Token;

        var processTask = _machineProcessActivityProvider.CaptureAsync(
            MachineProcessActivityBudget.Default,
            cancellationToken).AsTask();
        var systemCpuTask = CaptureSystemCpuActivitySafelyAsync(
            cancellationToken).AsTask();

        await Task.WhenAll(processTask, systemCpuTask).ConfigureAwait(false);
        return new MachineProcessActivityCaptureBundle(
            await processTask.ConfigureAwait(false),
            await systemCpuTask.ConfigureAwait(false));
    }

    private async ValueTask<SystemCpuActivityResult>
        CaptureSystemCpuActivitySafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _systemCpuActivityProvider.CaptureAsync(
                SystemCpuActivityBudget.Default,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SystemCpuActivityResult.Unavailable(
                SystemCpuActivityBudget.Default,
                SystemCpuActivityStatus.Unavailable,
                TimeSpan.Zero,
                $"Supplementary Windows system CPU interval failed: {exception.Message}");
        }
    }
}

internal sealed record MachineProcessActivityCaptureBundle(
    MachineProcessActivityResult ProcessActivity,
    SystemCpuActivityResult SystemCpuActivity);
