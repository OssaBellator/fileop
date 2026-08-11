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
        : this(
            budget,
            status,
            report,
            stopReason,
            lossState,
            lostEventCount,
            lossState == DiskIoCaptureLossState.Unknown ? null : 0,
            providerOverheadDuration,
            detail)
    {
    }

    public DiskIoCaptureResult(
        DiskIoCaptureBudget budget,
        DiskIoCaptureStatus status,
        DiskIoAttributionReport? report,
        DiskIoCaptureStopReason? stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount,
        long? lostBufferCount,
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
            throw new ArgumentOutOfRangeException(nameof(lossState), lossState, "Unsupported disk-I/O trace-loss state.");
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

        if (lostBufferCount is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lostBufferCount),
                lostBufferCount,
                "Disk-I/O lost-buffer count cannot be negative.");
        }

        if (status == DiskIoCaptureStatus.Completed)
        {
            ValidateCompleted(
                budget,
                report,
                stopReason,
                lossState,
                lostEventCount,
                lostBufferCount);
        }
        else
        {
            ValidateUnavailable(
                report,
                stopReason,
                lossState,
                lostEventCount,
                lostBufferCount);
        }

        Budget = budget;
        Status = status;
        Report = report;
        StopReason = stopReason;
        LossState = lossState;
        LostEventCount = lostEventCount;
        LostBufferCount = lostBufferCount;
        ProviderOverheadDuration = providerOverheadDuration;
        Detail = detail;
    }

    public DiskIoCaptureBudget Budget { get; }

    public DiskIoCaptureStatus Status { get; }

    public DiskIoAttributionReport? Report { get; }

    public DiskIoCaptureStopReason? StopReason { get; }

    public DiskIoCaptureLossState LossState { get; }

    public long? LostEventCount { get; }

    public long? LostBufferCount { get; }

    public TimeSpan? ProviderOverheadDuration { get; }

    public string Detail { get; }

    public IReadOnlyList<DiskIoDiskResponseTiming> ResponseTimings { get; private init; } =
        Array.Empty<DiskIoDiskResponseTiming>();

    public IReadOnlyList<DiskIoDiskProcessResponseTiming> ProcessResponseTimings { get; private init; } =
        Array.Empty<DiskIoDiskProcessResponseTiming>();

    private bool ResponseTimingsAttached { get; init; }

    public bool EvidenceMayBeIncomplete =>
        Status == DiskIoCaptureStatus.Completed &&
        (StopReason == DiskIoCaptureStopReason.ObservationLimitReached ||
         LossState != DiskIoCaptureLossState.NoneObserved);

    public DiskIoCaptureResult WithResponseTimings(
        IReadOnlyList<DiskIoDiskResponseTiming> responseTimings)
    {
        ArgumentNullException.ThrowIfNull(responseTimings);
        if (Status != DiskIoCaptureStatus.Completed || Report is null)
        {
            throw new InvalidOperationException(
                "Disk-I/O response timing can be attached only to a completed capture result.");
        }

        var snapshot = responseTimings.ToArray();
        if (snapshot.Length != Report.Disks.Count)
        {
            throw new ArgumentException(
                "Disk-I/O response timing must contain exactly one row for every physical disk in the attribution report.",
                nameof(responseTimings));
        }

        var reportDisks = Report.Disks.ToDictionary(static disk => disk.PhysicalDiskNumber);
        var seen = new HashSet<uint>();
        long sampleCount = 0;
        foreach (var timing in snapshot)
        {
            if (!seen.Add(timing.PhysicalDiskNumber) ||
                !reportDisks.TryGetValue(timing.PhysicalDiskNumber, out var disk))
            {
                throw new ArgumentException(
                    "Disk-I/O response timing contains a duplicate or unknown physical disk.",
                    nameof(responseTimings));
            }

            var readSamples = timing.Reads?.SampleCount ?? 0;
            var writeSamples = timing.Writes?.SampleCount ?? 0;
            var flushSamples = timing.Flushes?.SampleCount ?? 0;
            if (readSamples != disk.ReadOperations ||
                writeSamples != disk.WriteOperations ||
                flushSamples != disk.FlushOperations)
            {
                throw new ArgumentException(
                    $"Disk {timing.PhysicalDiskNumber} response timing sample counts do not match the attribution operation counts.",
                    nameof(responseTimings));
            }

            sampleCount += timing.SampleCount;
        }

        if (sampleCount != Report.AcceptedEventCount)
        {
            throw new ArgumentException(
                "Disk-I/O response timing sample count does not match the completed attribution report.",
                nameof(responseTimings));
        }

        return this with
        {
            ResponseTimings = snapshot,
            ResponseTimingsAttached = true,
        };
    }

    public DiskIoCaptureResult WithProcessResponseTimings(
        IReadOnlyList<DiskIoDiskProcessResponseTiming> processResponseTimings)
    {
        ArgumentNullException.ThrowIfNull(processResponseTimings);
        if (Status != DiskIoCaptureStatus.Completed || Report is null)
        {
            throw new InvalidOperationException(
                "Disk-I/O process response timing can be attached only to a completed capture result.");
        }
        if (!ResponseTimingsAttached)
        {
            throw new InvalidOperationException(
                "Disk-I/O disk response timing must be attached before process response timing.");
        }

        var snapshot = processResponseTimings.ToArray();
        if (snapshot.Length != Report.Disks.Count)
        {
            throw new ArgumentException(
                "Disk-I/O process response timing must contain exactly one row for every physical disk in the attribution report.",
                nameof(processResponseTimings));
        }

        var responseByDisk = ResponseTimings.ToDictionary(static timing => timing.PhysicalDiskNumber);
        long sampleCount = 0;
        for (var diskIndex = 0; diskIndex < Report.Disks.Count; diskIndex++)
        {
            var disk = Report.Disks[diskIndex];
            var timing = snapshot[diskIndex];
            if (timing.PhysicalDiskNumber != disk.PhysicalDiskNumber ||
                !responseByDisk.TryGetValue(disk.PhysicalDiskNumber, out var diskTiming))
            {
                throw new ArgumentException(
                    "Disk-I/O process response timing must preserve attribution disk order and identity.",
                    nameof(processResponseTimings));
            }
            if (timing.TotalSamples != disk.TotalOperations ||
                timing.TotalSamples != diskTiming.SampleCount ||
                timing.UnattributedReadSamples != disk.UnattributedReadOperations ||
                timing.UnattributedWriteSamples != disk.UnattributedWriteOperations ||
                timing.UnattributedFlushSamples != disk.UnattributedFlushOperations ||
                timing.OtherIdentifiedReadSamples != disk.OtherIdentifiedReadOperations ||
                timing.OtherIdentifiedWriteSamples != disk.OtherIdentifiedWriteOperations ||
                timing.OtherIdentifiedFlushSamples != disk.OtherIdentifiedFlushOperations ||
                timing.Owners.Count != disk.Owners.Count)
            {
                throw new ArgumentException(
                    $"Disk {disk.PhysicalDiskNumber} process response timing does not match attribution/timing counts.",
                    nameof(processResponseTimings));
            }

            for (var ownerIndex = 0; ownerIndex < disk.Owners.Count; ownerIndex++)
            {
                var attributionOwner = disk.Owners[ownerIndex];
                var timingOwner = timing.Owners[ownerIndex];
                if (timingOwner.Owner != attributionOwner.Owner ||
                    (timingOwner.Reads?.SampleCount ?? 0) != attributionOwner.ReadOperations ||
                    (timingOwner.Writes?.SampleCount ?? 0) != attributionOwner.WriteOperations ||
                    (timingOwner.Flushes?.SampleCount ?? 0) != attributionOwner.FlushOperations)
                {
                    throw new ArgumentException(
                        $"Disk {disk.PhysicalDiskNumber} process response timing does not preserve visible attribution owner order/counts.",
                        nameof(processResponseTimings));
                }
            }

            sampleCount += timing.TotalSamples;
        }

        if (sampleCount != Report.AcceptedEventCount)
        {
            throw new ArgumentException(
                "Disk-I/O process response timing sample count does not match the completed attribution report.",
                nameof(processResponseTimings));
        }

        return this with
        {
            ProcessResponseTimings = snapshot,
        };
    }

    public static DiskIoCaptureResult Completed(
        DiskIoCaptureBudget budget,
        DiskIoAttributionReport report,
        DiskIoCaptureStopReason stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount,
        TimeSpan? providerOverheadDuration,
        string detail) =>
        Completed(
            budget,
            report,
            stopReason,
            lossState,
            lostEventCount,
            lossState == DiskIoCaptureLossState.Unknown ? null : 0,
            providerOverheadDuration,
            detail);

    public static DiskIoCaptureResult Completed(
        DiskIoCaptureBudget budget,
        DiskIoAttributionReport report,
        DiskIoCaptureStopReason stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount,
        long? lostBufferCount,
        TimeSpan? providerOverheadDuration,
        string detail) =>
        new(
            budget,
            DiskIoCaptureStatus.Completed,
            report,
            stopReason,
            lossState,
            lostEventCount,
            lostBufferCount,
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
            lostBufferCount: null,
            providerOverheadDuration,
            detail);
    }

    private static void ValidateCompleted(
        DiskIoCaptureBudget budget,
        DiskIoAttributionReport? report,
        DiskIoCaptureStopReason? stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount,
        long? lostBufferCount)
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
            case DiskIoCaptureLossState.NoneObserved
                when lostEventCount != 0 || lostBufferCount != 0:
                throw new ArgumentException(
                    "No-loss disk-I/O capture results must report zero lost events and zero lost buffers.");
            case DiskIoCaptureLossState.Observed
                when lostEventCount is not > 0 && lostBufferCount is not > 0:
                throw new ArgumentException(
                    "Observed disk-I/O trace loss requires a positive lost-event or lost-buffer count.");
            case DiskIoCaptureLossState.Unknown
                when lostEventCount is not null || lostBufferCount is not null:
                throw new ArgumentException(
                    "Unknown disk-I/O trace loss must not invent event or buffer loss counts.");
        }
    }

    private static void ValidateUnavailable(
        DiskIoAttributionReport? report,
        DiskIoCaptureStopReason? stopReason,
        DiskIoCaptureLossState lossState,
        long? lostEventCount,
        long? lostBufferCount)
    {
        if (report is not null || stopReason is not null)
        {
            throw new ArgumentException(
                "Unavailable disk-I/O capture results cannot carry an attribution report or stop reason.");
        }

        if (lossState != DiskIoCaptureLossState.Unknown ||
            lostEventCount is not null ||
            lostBufferCount is not null)
        {
            throw new ArgumentException(
                "Unavailable disk-I/O capture results must leave trace-loss evidence unknown.");
        }
    }
}

public interface IDiskIoAttributionProvider
{
    ValueTask<DiskIoCaptureResult> CaptureAsync(
        DiskIoCaptureBudget budget,
        CancellationToken cancellationToken = default);
}
