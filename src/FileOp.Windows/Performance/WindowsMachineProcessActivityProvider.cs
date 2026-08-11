using System.ComponentModel;
using System.Diagnostics;
using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal interface IWindowsMachineProcessSnapshotSource
{
    MachineProcessActivityFrame Capture(int maxProcessSnapshots);
}

internal sealed class WindowsMachineProcessSnapshotSource : IWindowsMachineProcessSnapshotSource
{
    private readonly Func<DateTimeOffset> _utcNow;

    public WindowsMachineProcessSnapshotSource()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    internal WindowsMachineProcessSnapshotSource(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public MachineProcessActivityFrame Capture(int maxProcessSnapshots)
    {
        if (maxProcessSnapshots <= 0 ||
            maxProcessSnapshots > MachineProcessActivityBudget.MaximumProcessSnapshots)
        {
            throw new ArgumentOutOfRangeException(nameof(maxProcessSnapshots));
        }

        var processes = Process.GetProcesses();
        try
        {
            var candidates = new List<(int ProcessId, Process Process)>(processes.Length);
            var inaccessible = 0;
            foreach (var process in processes)
            {
                try
                {
                    candidates.Add((process.Id, process));
                }
                catch (Exception exception) when (IsPerProcessAccessFailure(exception))
                {
                    inaccessible = SaturatingIncrement(inaccessible);
                }
            }

            candidates.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));
            var snapshotCapReached = candidates.Count > maxProcessSnapshots;
            var captured = new List<MachineProcessCounterSnapshot>(
                Math.Min(candidates.Count, maxProcessSnapshots));
            foreach (var candidate in candidates.Take(maxProcessSnapshots))
            {
                try
                {
                    candidate.Process.Refresh();
                    var startedAt = new DateTimeOffset(
                        candidate.Process.StartTime.ToUniversalTime());
                    var imageName = candidate.Process.ProcessName;
                    var totalProcessorTime = candidate.Process.TotalProcessorTime;
                    var workingSetBytes = candidate.Process.WorkingSet64;
                    var privateMemoryBytes = candidate.Process.PrivateMemorySize64;
                    var threadCount = candidate.Process.Threads.Count;
                    if (totalProcessorTime < TimeSpan.Zero ||
                        workingSetBytes < 0 ||
                        privateMemoryBytes < 0 ||
                        threadCount < 0)
                    {
                        inaccessible = SaturatingIncrement(inaccessible);
                        continue;
                    }

                    captured.Add(new MachineProcessCounterSnapshot(
                        new MachineProcessIdentity(
                            candidate.ProcessId,
                            startedAt,
                            string.IsNullOrWhiteSpace(imageName) ? null : imageName),
                        totalProcessorTime,
                        workingSetBytes,
                        privateMemoryBytes,
                        threadCount));
                }
                catch (Exception exception) when (IsPerProcessAccessFailure(exception))
                {
                    inaccessible = SaturatingIncrement(inaccessible);
                }
            }

            return new MachineProcessActivityFrame(
                _utcNow().ToUniversalTime(),
                processes.Length,
                inaccessible,
                snapshotCapReached,
                captured);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool IsPerProcessAccessFailure(Exception exception) =>
        exception is Win32Exception or InvalidOperationException or NotSupportedException;

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;
}

public sealed class WindowsMachineProcessActivityProvider : IMachineProcessActivityProvider
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly IWindowsMachineProcessSnapshotSource _snapshotSource;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly int _captureProcessId;

    public WindowsMachineProcessActivityProvider()
        : this(
            new WindowsMachineProcessSnapshotSource(),
            static (duration, cancellationToken) => Task.Delay(duration, cancellationToken),
            Environment.ProcessId)
    {
    }

    internal WindowsMachineProcessActivityProvider(
        IWindowsMachineProcessSnapshotSource snapshotSource,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        int captureProcessId)
    {
        _snapshotSource = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        if (captureProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureProcessId));
        }
        _captureProcessId = captureProcessId;
    }

    public async ValueTask<MachineProcessActivityResult> CaptureAsync(
        MachineProcessActivityBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return MachineProcessActivityResult.Unavailable(
                budget,
                MachineProcessActivityStatus.Unsupported,
                "Bounded machine-process activity counters are available only on Windows.");
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captureStart = Stopwatch.GetTimestamp();
            MachineProcessActivityFrame start;
            MachineProcessActivityFrame end;
            try
            {
                start = _snapshotSource.Capture(budget.MaxProcessSnapshots);
                await _delayAsync(budget.SamplingDelay, cancellationToken).ConfigureAwait(false);
                end = _snapshotSource.Capture(budget.MaxProcessSnapshots);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                return MachineProcessActivityResult.Unavailable(
                    budget,
                    MachineProcessActivityStatus.Unavailable,
                    $"Machine-process activity counters could not be captured: {exception.Message}");
            }

            var elapsed = Stopwatch.GetElapsedTime(captureStart);
            var providerOverhead = elapsed > budget.SamplingDelay
                ? elapsed - budget.SamplingDelay
                : TimeSpan.Zero;
            var report = MachineProcessActivityAnalyzer.Analyze(
                budget,
                start,
                end,
                _captureProcessId,
                providerOverhead);
            var detail =
                $"Matched {report.StableMatchedProcessCount:N0} stable machine process instance(s) across a {budget.SamplingDelay.TotalMilliseconds:N0} ms bounded delay; " +
                $"{report.StartedDuringSampleCount:N0} appeared and {report.ExitedDuringSampleCount:N0} disappeared between readable frames. " +
                $"Counter access was unavailable for {report.StartInaccessibleProcessCount:N0}/{report.EndInaccessibleProcessCount:N0} start/end process read(s); " +
                $"snapshot cap {(report.SnapshotCapReached ? "was reached" : "was not reached")}. " +
                $"Provider overhead beyond the requested delay was {report.ProviderOverheadDuration.TotalMilliseconds:N2} ms.";
            return MachineProcessActivityResult.Completed(budget, report, detail);
        }
        finally
        {
            _captureGate.Release();
        }
    }
}
