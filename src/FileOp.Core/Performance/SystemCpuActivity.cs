namespace FileOp.Core.Performance;

public sealed record SystemCpuActivityBudget
{
    public static readonly TimeSpan DefaultSamplingDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MinimumSamplingDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumSamplingDelay = TimeSpan.FromSeconds(3);

    public static SystemCpuActivityBudget Default { get; } = new(DefaultSamplingDelay);

    public SystemCpuActivityBudget(TimeSpan samplingDelay)
    {
        if (samplingDelay < MinimumSamplingDelay || samplingDelay > MaximumSamplingDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplingDelay),
                samplingDelay,
                $"System CPU sampling delay must be between {MinimumSamplingDelay.TotalMilliseconds:N0} ms and {MaximumSamplingDelay.TotalSeconds:N0} s.");
        }
        SamplingDelay = samplingDelay;
    }

    public TimeSpan SamplingDelay { get; }
}

public sealed record SystemCpuTimeSnapshot
{
    public SystemCpuTimeSnapshot(
        DateTimeOffset observedAt,
        ulong idleTime100Nanoseconds,
        ulong kernelTime100Nanoseconds,
        ulong userTime100Nanoseconds)
    {
        if (idleTime100Nanoseconds > kernelTime100Nanoseconds)
        {
            throw new ArgumentException(
                "GetSystemTimes idle time cannot exceed kernel time because Windows includes idle time in the kernel counter.");
        }

        ObservedAt = observedAt.ToUniversalTime();
        IdleTime100Nanoseconds = idleTime100Nanoseconds;
        KernelTime100Nanoseconds = kernelTime100Nanoseconds;
        UserTime100Nanoseconds = userTime100Nanoseconds;
    }

    public DateTimeOffset ObservedAt { get; }
    public ulong IdleTime100Nanoseconds { get; }
    public ulong KernelTime100Nanoseconds { get; }
    public ulong UserTime100Nanoseconds { get; }
}

public sealed record SystemCpuActivityEvidence
{
    public SystemCpuActivityEvidence(
        DateTimeOffset startObservedAt,
        DateTimeOffset endObservedAt,
        TimeSpan totalProcessorTimeDelta,
        TimeSpan idleProcessorTimeDelta)
    {
        if (endObservedAt < startObservedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(endObservedAt));
        }
        if (totalProcessorTimeDelta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(totalProcessorTimeDelta));
        }
        if (idleProcessorTimeDelta < TimeSpan.Zero ||
            idleProcessorTimeDelta > totalProcessorTimeDelta)
        {
            throw new ArgumentOutOfRangeException(nameof(idleProcessorTimeDelta));
        }

        StartObservedAt = startObservedAt.ToUniversalTime();
        EndObservedAt = endObservedAt.ToUniversalTime();
        TotalProcessorTimeDelta = totalProcessorTimeDelta;
        IdleProcessorTimeDelta = idleProcessorTimeDelta;
    }

    public DateTimeOffset StartObservedAt { get; }
    public DateTimeOffset EndObservedAt { get; }
    public TimeSpan ObservationWallDuration => EndObservedAt - StartObservedAt;
    public TimeSpan TotalProcessorTimeDelta { get; }
    public TimeSpan IdleProcessorTimeDelta { get; }
    public TimeSpan BusyProcessorTimeDelta => TotalProcessorTimeDelta - IdleProcessorTimeDelta;

    public double? BusyPercent =>
        TotalProcessorTimeDelta.Ticks == 0
            ? null
            : BusyProcessorTimeDelta.Ticks * 100d / TotalProcessorTimeDelta.Ticks;
}

public enum SystemCpuActivityStatus
{
    Completed,
    Unsupported,
    Unavailable,
}

public sealed record SystemCpuActivityResult
{
    private SystemCpuActivityResult(
        SystemCpuActivityBudget budget,
        SystemCpuActivityStatus status,
        SystemCpuActivityEvidence? evidence,
        TimeSpan providerOverheadDuration,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if ((status == SystemCpuActivityStatus.Completed) != (evidence is not null))
        {
            throw new ArgumentException(
                "Completed system CPU results require evidence; unavailable results cannot carry it.",
                nameof(evidence));
        }
        if (providerOverheadDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(providerOverheadDuration));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        Budget = budget;
        Status = status;
        Evidence = evidence;
        ProviderOverheadDuration = providerOverheadDuration;
        Detail = detail;
    }

    public SystemCpuActivityBudget Budget { get; }
    public SystemCpuActivityStatus Status { get; }
    public SystemCpuActivityEvidence? Evidence { get; }
    public TimeSpan ProviderOverheadDuration { get; }
    public string Detail { get; }

    public static SystemCpuActivityResult Completed(
        SystemCpuActivityBudget budget,
        SystemCpuActivityEvidence evidence,
        TimeSpan providerOverheadDuration,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new SystemCpuActivityResult(
            budget,
            SystemCpuActivityStatus.Completed,
            evidence,
            providerOverheadDuration,
            detail);
    }

    public static SystemCpuActivityResult Unavailable(
        SystemCpuActivityBudget budget,
        SystemCpuActivityStatus status,
        TimeSpan providerOverheadDuration,
        string detail)
    {
        if (status == SystemCpuActivityStatus.Completed)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        return new SystemCpuActivityResult(
            budget,
            status,
            null,
            providerOverheadDuration,
            detail);
    }
}

public static class SystemCpuActivityAnalyzer
{
    public static SystemCpuActivityEvidence Analyze(
        SystemCpuTimeSnapshot start,
        SystemCpuTimeSnapshot end)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        if (end.ObservedAt < start.ObservedAt)
        {
            throw new InvalidDataException(
                "System CPU end observation precedes the start observation.");
        }
        if (end.IdleTime100Nanoseconds < start.IdleTime100Nanoseconds ||
            end.KernelTime100Nanoseconds < start.KernelTime100Nanoseconds ||
            end.UserTime100Nanoseconds < start.UserTime100Nanoseconds)
        {
            throw new InvalidDataException(
                "GetSystemTimes cumulative counters decreased across one bounded sample.");
        }

        var idleDelta = end.IdleTime100Nanoseconds - start.IdleTime100Nanoseconds;
        var kernelDelta = end.KernelTime100Nanoseconds - start.KernelTime100Nanoseconds;
        var userDelta = end.UserTime100Nanoseconds - start.UserTime100Nanoseconds;
        if (idleDelta > kernelDelta)
        {
            throw new InvalidDataException(
                "GetSystemTimes idle delta exceeds the kernel delta that includes idle time.");
        }

        var totalDelta = (UInt128)kernelDelta + userDelta;
        if (totalDelta > (UInt128)long.MaxValue || idleDelta > (ulong)long.MaxValue)
        {
            throw new InvalidDataException(
                "System CPU interval exceeds FileOp's TimeSpan evidence range.");
        }

        return new SystemCpuActivityEvidence(
            start.ObservedAt,
            end.ObservedAt,
            TimeSpan.FromTicks((long)totalDelta),
            TimeSpan.FromTicks((long)idleDelta));
    }
}

public interface ISystemCpuActivityProvider
{
    ValueTask<SystemCpuActivityResult> CaptureAsync(
        SystemCpuActivityBudget budget,
        CancellationToken cancellationToken = default);
}
