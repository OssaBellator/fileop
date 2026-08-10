namespace FileOp.Core.Performance;

public sealed record DiskIoCaptureBudget
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(5);

    public const int DefaultMaxObservations = 100_000;
    public const int MaximumObservations = 500_000;

    public static DiskIoCaptureBudget Default { get; } = new(
        DefaultDuration,
        DefaultMaxObservations,
        DiskIoAttributionAnalyzer.DefaultMaxOwnersPerDisk);

    public DiskIoCaptureBudget(
        TimeSpan duration,
        int maxObservations,
        int maxOwnersPerDisk)
    {
        if (duration < MinimumDuration || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                $"Disk-I/O capture duration must be between {MinimumDuration.TotalMilliseconds:N0} ms and {MaximumDuration.TotalSeconds:N0} s.");
        }

        if (maxObservations <= 0 || maxObservations > MaximumObservations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxObservations),
                maxObservations,
                $"Disk-I/O capture observation limit must be between 1 and {MaximumObservations:N0} events.");
        }

        if (maxOwnersPerDisk <= 0 || maxOwnersPerDisk > DiskIoAttributionAnalyzer.MaximumOwnersPerDisk)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOwnersPerDisk),
                maxOwnersPerDisk,
                $"Disk-I/O owner rows must be between 1 and {DiskIoAttributionAnalyzer.MaximumOwnersPerDisk} per physical disk.");
        }

        Duration = duration;
        MaxObservations = maxObservations;
        MaxOwnersPerDisk = maxOwnersPerDisk;
    }

    public TimeSpan Duration { get; }

    public int MaxObservations { get; }

    public int MaxOwnersPerDisk { get; }
}

public enum DiskIoCaptureStatus
{
    Completed,
    Unsupported,
    PermissionRequired,
    SessionUnavailable,
}

public enum DiskIoCaptureStopReason
{
    DurationElapsed,
    ObservationLimitReached,
}

public enum DiskIoCaptureLossState
{
    NoneObserved,
    Observed,
    Unknown,
}

public sealed record DiskIoCaptureResult
{
    public DiskIoCaptureResult(
        DiskIoCaptureBudget budget,
        DiskIoCaptureStatus status,
        DiskIoAttributionReport? report,
        DiskIoCaptureStopReason? stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount,
        TimeSpan? providerOverheadDuration,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported disk-I/O capture status.");
        }

        if (!Enum.IsDefined(lossState))
        {
            throw new ArgumentOutOfRangeException(nameof(lossState), lossState, "Unsupported disk-I/O event-loss state.");
        }

        if (stopReason is { } reason && !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(stopReason), stopReason, "Unsupported disk-I/O capture stop reason.");
        }

        if (providerOverheadDuration is { } overhead && overhead < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerOverheadDuration),
                providerOverheadDuration,
                "Disk-I/O provider overhead duration cannot be negative.");
        }

        if (lostEventCount is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lostEventCount),
                lostEventCount,
                "Disk-I/O lost-event count cannot be negative.");
        }

        if (status == DiskIoCaptureStatus.Completed)
        {
            ValidateCompleted(
                budget,
                report,
                stopReason,
                lossState,
                lostEventCount);
        }
        else
        {
            ValidateUnavailable(report, stopReason, lossState, lostEventCount);
        }

        Budget = budget;
        Status = status;
        Report = report;
        StopReason = stopReason;
        LossState = lossState;
        LostEventCount = lostEventCount;
        ProviderOverheadDuration = providerOverheadDuration;
        Detail = detail;
    }

    public DiskIoCaptureBudget Budget { get; }

    public DiskIoCaptureStatus Status { get; }

    public DiskIoAttributionReport? Report { get; }

    public DiskIoCaptureStopReason? StopReason { get; }

    public DiskIoCaptureLossState LossState { get; }

    public long? LostEventCount { get; }

    public TimeSpan? ProviderOverheadDuration { get; }

    public string Detail { get; }

    public bool EvidenceMayBeIncomplete =>
        Status == DiskIoCaptureStatus.Completed &&
        (StopReason == DiskIoCaptureStopReason.ObservationLimitReached ||
         LossState != DiskIoCaptureLossState.NoneObserved);

    public static DiskIoCaptureResult Completed(
        DiskIoCaptureBudget budget,
        DiskIoAttributionReport report,
        DiskIoCaptureStopReason stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount,
        TimeSpan? providerOverheadDuration,
        string detail) =>
        new(
            budget,
            DiskIoCaptureStatus.Completed,
            report,
            stopReason,
            lossState,
            lostEventCount,
            providerOverheadDuration,
            detail);

    public static DiskIoCaptureResult Unavailable(
        DiskIoCaptureBudget budget,
        DiskIoCaptureStatus status,
        TimeSpan? providerOverheadDuration,
        string detail)
    {
        if (status == DiskIoCaptureStatus.Completed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Completed disk-I/O captures must use the Completed result factory.");
        }

        return new DiskIoCaptureResult(
            budget,
            status,
            report: null,
            stopReason: null,
            DiskIoCaptureLossState.Unknown,
            lostEventCount: null,
            providerOverheadDuration,
            detail);
    }

    private static void ValidateCompleted(
        DiskIoCaptureBudget budget,
        DiskIoAttributionReport? report,
        DiskIoCaptureStopReason? stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount)
    {
        if (report is null)
        {
            throw new ArgumentException(
                "Completed disk-I/O capture results require an attribution report.",
                nameof(report));
        }

        if (stopReason is null)
        {
            throw new ArgumentException(
                "Completed disk-I/O capture results require an explicit stop reason.",
                nameof(stopReason));
        }

        if (report.ObservationDuration < TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Disk-I/O attribution report cannot have a negative observation duration.",
                nameof(report));
        }

        if (report.ObservationDuration > budget.Duration)
        {
            throw new ArgumentException(
                "Disk-I/O attribution report exceeds the requested capture duration.",
                nameof(report));
        }

        if (report.AcceptedEventCount < 0)
        {
            throw new ArgumentException(
                "Disk-I/O attribution report cannot have a negative accepted-event count.",
                nameof(report));
        }

        if (report.AcceptedEventCount > budget.MaxObservations)
        {
            throw new ArgumentException(
                "Disk-I/O attribution report exceeds the requested observation limit.",
                nameof(report));
        }

        if (report.MaxOwnersPerDisk != budget.MaxOwnersPerDisk)
        {
            throw new ArgumentException(
                "Disk-I/O attribution report owner-row limit does not match the capture budget.",
                nameof(report));
        }

        if (stopReason == DiskIoCaptureStopReason.DurationElapsed &&
            report.ObservationDuration != budget.Duration)
        {
            throw new ArgumentException(
                "Duration-elapsed stop reason requires the report to cover the full requested capture duration.",
                nameof(report));
        }

        if (stopReason == DiskIoCaptureStopReason.ObservationLimitReached &&
            report.AcceptedEventCount != budget.MaxObservations)
        {
            throw new ArgumentException(
                "Observation-limit stop reason requires the report to contain the full requested observation limit.",
                nameof(report));
        }

        switch (lossState)
        {
            case DiskIoCaptureLossState.NoneObserved when lostEventCount != 0:
                throw new ArgumentException(
                    "No-loss disk-I/O capture results must report a lost-event count of zero.",
                    nameof(lostEventCount));
            case DiskIoCaptureLossState.Observed when lostEventCount is not > 0:
                throw new ArgumentException(
                    "Observed disk-I/O event loss requires a positive lost-event count.",
                    nameof(lostEventCount));
            case DiskIoCaptureLossState.Unknown when lostEventCount is not null:
                throw new ArgumentException(
                    "Unknown disk-I/O event loss must not invent a lost-event count.",
                    nameof(lostEventCount));
        }
    }

    private static void ValidateUnavailable(
        DiskIoAttributionReport? report,
        DiskIoCaptureStopReason? stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount)
    {
        if (report is not null || stopReason is not null)
        {
            throw new ArgumentException(
                "Unavailable disk-I/O capture results cannot carry an attribution report or stop reason.");
        }

        if (lossState != DiskIoCaptureLossState.Unknown || lostEventCount is not null)
        {
            throw new ArgumentException(
                "Unavailable disk-I/O capture results must leave event-loss evidence unknown.");
        }
    }
}

public interface IDiskIoAttributionProvider
{
    ValueTask<DiskIoCaptureResult> CaptureAsync(
        DiskIoCaptureBudget budget,
        CancellationToken cancellationToken = default);
}
