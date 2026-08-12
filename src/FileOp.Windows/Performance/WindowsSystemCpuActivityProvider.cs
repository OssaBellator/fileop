using System.Diagnostics;
using System.Runtime.InteropServices;
using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal sealed record WindowsSystemCpuTimeQueryResult(
    SystemCpuTimeSnapshot? Snapshot,
    int Win32Error)
{
    public bool Succeeded => Snapshot is not null;
}

internal interface IWindowsSystemCpuTimeSource
{
    WindowsSystemCpuTimeQueryResult Query();
}

internal sealed class WindowsSystemCpuTimeSource : IWindowsSystemCpuTimeSource
{
    private readonly Func<DateTimeOffset> _utcNow;

    public WindowsSystemCpuTimeSource()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    internal WindowsSystemCpuTimeSource(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public WindowsSystemCpuTimeQueryResult Query()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return new WindowsSystemCpuTimeQueryResult(
                null,
                Marshal.GetLastWin32Error());
        }

        try
        {
            return new WindowsSystemCpuTimeQueryResult(
                new SystemCpuTimeSnapshot(
                    _utcNow().ToUniversalTime(),
                    ToUInt64(idle),
                    ToUInt64(kernel),
                    ToUInt64(user)),
                0);
        }
        catch (ArgumentException)
        {
            return new WindowsSystemCpuTimeQueryResult(null, 13); // ERROR_INVALID_DATA.
        }
    }

    private static ulong ToUInt64(FileTimeNative value) =>
        ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimeNative
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTimeNative lpIdleTime,
        out FileTimeNative lpKernelTime,
        out FileTimeNative lpUserTime);
}

public sealed class WindowsSystemCpuActivityProvider : ISystemCpuActivityProvider
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly IWindowsSystemCpuTimeSource _source;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public WindowsSystemCpuActivityProvider()
        : this(
            new WindowsSystemCpuTimeSource(),
            static (duration, cancellationToken) => Task.Delay(duration, cancellationToken))
    {
    }

    internal WindowsSystemCpuActivityProvider(
        IWindowsSystemCpuTimeSource source,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public async ValueTask<SystemCpuActivityResult> CaptureAsync(
        SystemCpuActivityBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return SystemCpuActivityResult.Unavailable(
                budget,
                SystemCpuActivityStatus.Unsupported,
                TimeSpan.Zero,
                "Windows GetSystemTimes CPU interval evidence is available only on Windows.");
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captureStart = Stopwatch.GetTimestamp();
            var startQuery = _source.Query();
            if (!startQuery.Succeeded || startQuery.Snapshot is not { } startSnapshot)
            {
                return SystemCpuActivityResult.Unavailable(
                    budget,
                    SystemCpuActivityStatus.Unavailable,
                    Stopwatch.GetElapsedTime(captureStart),
                    $"The first GetSystemTimes observation failed with Win32 error {startQuery.Win32Error}; no sampling delay was started.");
            }

            await _delayAsync(budget.SamplingDelay, cancellationToken).ConfigureAwait(false);

            var endQuery = _source.Query();
            var elapsed = Stopwatch.GetElapsedTime(captureStart);
            var overhead = elapsed > budget.SamplingDelay
                ? elapsed - budget.SamplingDelay
                : TimeSpan.Zero;
            if (!endQuery.Succeeded || endQuery.Snapshot is not { } endSnapshot)
            {
                return SystemCpuActivityResult.Unavailable(
                    budget,
                    SystemCpuActivityStatus.Unavailable,
                    overhead,
                    $"The second GetSystemTimes observation failed with Win32 error {endQuery.Win32Error} after the bounded delay.");
            }

            SystemCpuActivityEvidence evidence;
            try
            {
                evidence = SystemCpuActivityAnalyzer.Analyze(startSnapshot, endSnapshot);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or ArgumentException)
            {
                return SystemCpuActivityResult.Unavailable(
                    budget,
                    SystemCpuActivityStatus.Unavailable,
                    overhead,
                    $"GetSystemTimes returned inconsistent interval evidence: {exception.Message}");
            }

            return SystemCpuActivityResult.Completed(
                budget,
                evidence,
                overhead,
                $"GetSystemTimes sampled one {budget.SamplingDelay.TotalMilliseconds:N0} ms bounded interval. Windows kernel time includes idle time, so FileOp derives busy processor time as delta(kernel + user) minus delta(idle); the derived busy percentage is interval evidence, not a pressure or health score. On systems with more than 64 logical processors, GetSystemTimes can be scoped to the caller thread's primary processor group. Provider overhead beyond the requested delay was {overhead.TotalMilliseconds:N2} ms.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            TypeInitializationException)
        {
            return SystemCpuActivityResult.Unavailable(
                budget,
                SystemCpuActivityStatus.Unsupported,
                TimeSpan.Zero,
                $"GetSystemTimes is unavailable in this Windows environment: {exception.Message}");
        }
        finally
        {
            _captureGate.Release();
        }
    }
}
