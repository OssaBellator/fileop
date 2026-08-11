namespace FileOp.Core.Performance;

public sealed record MachineProcessActivityBudget
{
    public static readonly TimeSpan DefaultSamplingDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MinimumSamplingDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumSamplingDelay = TimeSpan.FromSeconds(3);

    public const int DefaultMaxProcessSnapshots = 1024;
    public const int MaximumProcessSnapshots = 4096;
    public const int DefaultMaxRows = 20;
    public const int MaximumRows = 100;

    public static MachineProcessActivityBudget Default { get; } = new(
        DefaultSamplingDelay,
        DefaultMaxProcessSnapshots,
        DefaultMaxRows);

    public MachineProcessActivityBudget(
        TimeSpan samplingDelay,
        int maxProcessSnapshots,
        int maxRows)
    {
        if (samplingDelay < MinimumSamplingDelay || samplingDelay > MaximumSamplingDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplingDelay),
                samplingDelay,
                $"Machine-process sampling delay must be between {MinimumSamplingDelay.TotalMilliseconds:N0} ms and {MaximumSamplingDelay.TotalSeconds:N0} s.");
        }
        if (maxProcessSnapshots <= 0 || maxProcessSnapshots > MaximumProcessSnapshots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxProcessSnapshots),
                maxProcessSnapshots,
                $"Machine-process snapshot limit must be between 1 and {MaximumProcessSnapshots:N0} process instances.");
        }
        if (maxRows <= 0 || maxRows > MaximumRows || maxRows > maxProcessSnapshots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRows),
                maxRows,
                $"Machine-process visible rows must be between 1 and {Math.Min(MaximumRows, maxProcessSnapshots):N0}.");
        }

        SamplingDelay = samplingDelay;
        MaxProcessSnapshots = maxProcessSnapshots;
        MaxRows = maxRows;
    }

    public TimeSpan SamplingDelay { get; }
    public int MaxProcessSnapshots { get; }
    public int MaxRows { get; }
}

public sealed record MachineProcessIdentity
{
    public MachineProcessIdentity(int processId, DateTimeOffset startedAt, string? imageName)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                processId,
                "A machine-process identity requires a positive process ID.");
        }
        if (imageName is { Length: > 0 } && string.IsNullOrWhiteSpace(imageName))
        {
            throw new ArgumentException(
                "A machine-process image name cannot contain only whitespace.",
                nameof(imageName));
        }

        ProcessId = processId;
        StartedAt = startedAt.ToUniversalTime();
        ImageName = imageName;
    }

    public int ProcessId { get; }
    public DateTimeOffset StartedAt { get; }
    public string? ImageName { get; }
}

public sealed record MachineProcessCounterSnapshot
{
    public MachineProcessCounterSnapshot(
        MachineProcessIdentity identity,
        TimeSpan totalProcessorTime,
        long workingSetBytes,
        long privateMemoryBytes,
        int threadCount)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (totalProcessorTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalProcessorTime),
                totalProcessorTime,
                "Machine-process total processor time cannot be negative.");
        }
        if (workingSetBytes < 0 || privateMemoryBytes < 0 || threadCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingSetBytes),
                "Machine-process resource counters cannot be negative.");
        }

        Identity = identity;
        TotalProcessorTime = totalProcessorTime;
        WorkingSetBytes = workingSetBytes;
        PrivateMemoryBytes = privateMemoryBytes;
        ThreadCount = threadCount;
    }

    public MachineProcessIdentity Identity { get; }
    public TimeSpan TotalProcessorTime { get; }
    public long WorkingSetBytes { get; }
    public long PrivateMemoryBytes { get; }
    public int ThreadCount { get; }
}

public sealed record MachineProcessActivityFrame
{
    public MachineProcessActivityFrame(
        DateTimeOffset capturedAt,
        int enumeratedProcessCount,
        int inaccessibleProcessCount,
        bool snapshotCapReached,
        IReadOnlyList<MachineProcessCounterSnapshot> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (enumeratedProcessCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(enumeratedProcessCount));
        }
        if (inaccessibleProcessCount < 0 || inaccessibleProcessCount > enumeratedProcessCount)
        {
            throw new ArgumentOutOfRangeException(nameof(inaccessibleProcessCount));
        }
        if (processes.Count > enumeratedProcessCount)
        {
            throw new ArgumentException(
                "Captured machine-process counters cannot exceed the enumerated process count.",
                nameof(processes));
        }

        var capturedAtUtc = capturedAt.ToUniversalTime();
        var snapshot = processes.ToArray();
        var processIds = new HashSet<int>();
        var keys = new HashSet<ProcessKey>();
        foreach (var process in snapshot)
        {
            ArgumentNullException.ThrowIfNull(process);
            if (process.Identity.StartedAt > capturedAtUtc)
            {
                throw new ArgumentException(
                    $"Process {process.Identity.ProcessId} starts after its frame timestamp.",
                    nameof(processes));
            }
            if (!processIds.Add(process.Identity.ProcessId))
            {
                throw new ArgumentException(
                    $"A machine-process frame cannot contain PID {process.Identity.ProcessId} more than once, even with different start times.",
                    nameof(processes));
            }
            if (!keys.Add(ProcessKey.From(process.Identity)))
            {
                throw new ArgumentException(
                    "A machine-process frame cannot repeat a stable process instance.",
                    nameof(processes));
            }
        }

        CapturedAt = capturedAtUtc;
        EnumeratedProcessCount = enumeratedProcessCount;
        InaccessibleProcessCount = inaccessibleProcessCount;
        SnapshotCapReached = snapshotCapReached;
        Processes = snapshot;
    }

    public DateTimeOffset CapturedAt { get; }
    public int EnumeratedProcessCount { get; }
    public int InaccessibleProcessCount { get; }
    public bool SnapshotCapReached { get; }
    public IReadOnlyList<MachineProcessCounterSnapshot> Processes { get; }
}

public sealed record MachineProcessActivityRow
{
    public MachineProcessActivityRow(
        MachineProcessIdentity identity,
        TimeSpan processorTimeDelta,
        long workingSetBytes,
        long privateMemoryBytes,
        int threadCount,
        bool isCaptureProcess)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (processorTimeDelta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(processorTimeDelta));
        }
        if (workingSetBytes < 0 || privateMemoryBytes < 0 || threadCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingSetBytes),
                "Machine-process end-of-sample resource counters cannot be negative.");
        }

        Identity = identity;
        ProcessorTimeDelta = processorTimeDelta;
        WorkingSetBytes = workingSetBytes;
        PrivateMemoryBytes = privateMemoryBytes;
        ThreadCount = threadCount;
        IsCaptureProcess = isCaptureProcess;
    }

    public MachineProcessIdentity Identity { get; }
    public TimeSpan ProcessorTimeDelta { get; }
    public long WorkingSetBytes { get; }
    public long PrivateMemoryBytes { get; }
    public int ThreadCount { get; }
    public bool IsCaptureProcess { get; }
}

public sealed record MachineProcessActivityReport
{
    public MachineProcessActivityReport(
        MachineProcessActivityBudget budget,
        DateTimeOffset startFrameCapturedAt,
        DateTimeOffset endFrameCapturedAt,
        TimeSpan providerOverheadDuration,
        int startEnumeratedProcessCount,
        int endEnumeratedProcessCount,
        int startInaccessibleProcessCount,
        int endInaccessibleProcessCount,
        bool snapshotCapReached,
        int stableMatchedProcessCount,
        int startedDuringSampleCount,
        int exitedDuringSampleCount,
        IReadOnlyList<MachineProcessActivityRow> rows,
        int otherMatchedProcessCount,
        TimeSpan otherMatchedProcessorTime,
        TimeSpan? captureProcessProcessorTime,
        long? captureProcessWorkingSetBytes)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(rows);
        if (endFrameCapturedAt < startFrameCapturedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(endFrameCapturedAt));
        }
        if (providerOverheadDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(providerOverheadDuration));
        }
        if (startEnumeratedProcessCount < 0 || endEnumeratedProcessCount < 0 ||
            startInaccessibleProcessCount < 0 || endInaccessibleProcessCount < 0 ||
            stableMatchedProcessCount < 0 || startedDuringSampleCount < 0 ||
            exitedDuringSampleCount < 0 || otherMatchedProcessCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stableMatchedProcessCount),
                "Machine-process evidence counts cannot be negative.");
        }
        if (startInaccessibleProcessCount > startEnumeratedProcessCount ||
            endInaccessibleProcessCount > endEnumeratedProcessCount)
        {
            throw new ArgumentException(
                "Inaccessible process counts cannot exceed their frame enumeration counts.");
        }
        if (rows.Count > budget.MaxRows || rows.Count > stableMatchedProcessCount)
        {
            throw new ArgumentException(
                "Visible machine-process rows exceed the capture budget or matched-process count.",
                nameof(rows));
        }
        if (otherMatchedProcessCount != stableMatchedProcessCount - rows.Count)
        {
            throw new ArgumentException(
                "Other matched-process count must account for every stable matched process hidden by the row budget.",
                nameof(otherMatchedProcessCount));
        }
        if (otherMatchedProcessorTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(otherMatchedProcessorTime));
        }
        if (captureProcessProcessorTime is { } captureCpu && captureCpu < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(captureProcessProcessorTime));
        }
        if (captureProcessWorkingSetBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureProcessWorkingSetBytes));
        }
        if ((captureProcessProcessorTime is null) != (captureProcessWorkingSetBytes is null))
        {
            throw new ArgumentException(
                "Capture-process CPU and working-set evidence must be available or unavailable together.");
        }

        var rowSnapshot = rows.ToArray();
        var rowPids = new HashSet<int>();
        MachineProcessActivityRow? visibleCapture = null;
        foreach (var row in rowSnapshot)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (!rowPids.Add(row.Identity.ProcessId))
            {
                throw new ArgumentException(
                    $"Visible machine-process rows cannot contain PID {row.Identity.ProcessId} more than once.",
                    nameof(rows));
            }
            if (row.IsCaptureProcess)
            {
                if (visibleCapture is not null)
                {
                    throw new ArgumentException(
                        "Visible rows cannot identify more than one capture process.",
                        nameof(rows));
                }
                visibleCapture = row;
            }
        }
        if (visibleCapture is not null &&
            (visibleCapture.ProcessorTimeDelta != captureProcessProcessorTime ||
             visibleCapture.WorkingSetBytes != captureProcessWorkingSetBytes))
        {
            throw new ArgumentException(
                "Visible capture-process row must match observer CPU/working-set evidence.",
                nameof(rows));
        }

        Budget = budget;
        StartFrameCapturedAt = startFrameCapturedAt.ToUniversalTime();
        EndFrameCapturedAt = endFrameCapturedAt.ToUniversalTime();
        ProviderOverheadDuration = providerOverheadDuration;
        StartEnumeratedProcessCount = startEnumeratedProcessCount;
        EndEnumeratedProcessCount = endEnumeratedProcessCount;
        StartInaccessibleProcessCount = startInaccessibleProcessCount;
        EndInaccessibleProcessCount = endInaccessibleProcessCount;
        SnapshotCapReached = snapshotCapReached;
        StableMatchedProcessCount = stableMatchedProcessCount;
        StartedDuringSampleCount = startedDuringSampleCount;
        ExitedDuringSampleCount = exitedDuringSampleCount;
        Rows = rowSnapshot;
        OtherMatchedProcessCount = otherMatchedProcessCount;
        OtherMatchedProcessorTime = otherMatchedProcessorTime;
        CaptureProcessProcessorTime = captureProcessProcessorTime;
        CaptureProcessWorkingSetBytes = captureProcessWorkingSetBytes;
    }

    public MachineProcessActivityBudget Budget { get; }
    public DateTimeOffset StartFrameCapturedAt { get; }
    public DateTimeOffset EndFrameCapturedAt { get; }
    public TimeSpan ProviderOverheadDuration { get; }
    public int StartEnumeratedProcessCount { get; }
    public int EndEnumeratedProcessCount { get; }
    public int StartInaccessibleProcessCount { get; }
    public int EndInaccessibleProcessCount { get; }
    public bool SnapshotCapReached { get; }
    public int StableMatchedProcessCount { get; }
    public int StartedDuringSampleCount { get; }
    public int ExitedDuringSampleCount { get; }
    public IReadOnlyList<MachineProcessActivityRow> Rows { get; }
    public int OtherMatchedProcessCount { get; }
    public TimeSpan OtherMatchedProcessorTime { get; }
    public TimeSpan? CaptureProcessProcessorTime { get; }
    public long? CaptureProcessWorkingSetBytes { get; }

    public bool EvidenceMayBeIncomplete =>
        SnapshotCapReached ||
        StartInaccessibleProcessCount > 0 ||
        EndInaccessibleProcessCount > 0;

    public TimeSpan VisibleProcessorTime =>
        SumProcessorTime(Rows.Select(static row => row.ProcessorTimeDelta));

    public TimeSpan TotalMatchedProcessorTime =>
        SumProcessorTime([VisibleProcessorTime, OtherMatchedProcessorTime]);

    private static TimeSpan SumProcessorTime(IEnumerable<TimeSpan> durations)
    {
        long ticks = 0;
        foreach (var duration in durations)
        {
            var remaining = long.MaxValue - ticks;
            ticks += duration.Ticks > remaining ? remaining : duration.Ticks;
        }
        return TimeSpan.FromTicks(ticks);
    }
}

public enum MachineProcessActivityStatus
{
    Completed,
    Unsupported,
    Unavailable,
}

public sealed record MachineProcessActivityResult
{
    private MachineProcessActivityResult(
        MachineProcessActivityBudget budget,
        MachineProcessActivityStatus status,
        MachineProcessActivityReport? report,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if ((status == MachineProcessActivityStatus.Completed) != (report is not null))
        {
            throw new ArgumentException(
                "Completed machine-process results require a report; unavailable results cannot carry one.",
                nameof(report));
        }
        if (report is not null && report.Budget != budget)
        {
            throw new ArgumentException(
                "Machine-process report budget must match the result budget.",
                nameof(report));
        }

        Budget = budget;
        Status = status;
        Report = report;
        Detail = detail;
    }

    public MachineProcessActivityBudget Budget { get; }
    public MachineProcessActivityStatus Status { get; }
    public MachineProcessActivityReport? Report { get; }
    public string Detail { get; }

    public static MachineProcessActivityResult Completed(
        MachineProcessActivityBudget budget,
        MachineProcessActivityReport report,
        string detail) =>
        new(budget, MachineProcessActivityStatus.Completed, report, detail);

    public static MachineProcessActivityResult Unavailable(
        MachineProcessActivityBudget budget,
        MachineProcessActivityStatus status,
        string detail)
    {
        if (status == MachineProcessActivityStatus.Completed)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        return new MachineProcessActivityResult(budget, status, null, detail);
    }
}

public static class MachineProcessActivityAnalyzer
{
    public static MachineProcessActivityReport Analyze(
        MachineProcessActivityBudget budget,
        MachineProcessActivityFrame start,
        MachineProcessActivityFrame end,
        int captureProcessId,
        TimeSpan providerOverheadDuration)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        if (captureProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureProcessId));
        }
        if (end.CapturedAt < start.CapturedAt)
        {
            throw new InvalidDataException(
                "Machine-process end frame precedes the start frame.");
        }
        if (providerOverheadDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(providerOverheadDuration));
        }
        if (start.Processes.Count > budget.MaxProcessSnapshots ||
            end.Processes.Count > budget.MaxProcessSnapshots)
        {
            throw new InvalidDataException(
                "Machine-process frame exceeds the requested snapshot budget.");
        }

        var startByKey = start.Processes.ToDictionary(
            static process => ProcessKey.From(process.Identity));
        var endByKey = end.Processes.ToDictionary(
            static process => ProcessKey.From(process.Identity));
        var matched = new List<MatchedProcess>();
        foreach (var pair in startByKey)
        {
            if (!endByKey.TryGetValue(pair.Key, out var endProcess))
            {
                continue;
            }

            var startProcess = pair.Value;
            if (endProcess.TotalProcessorTime < startProcess.TotalProcessorTime)
            {
                throw new InvalidDataException(
                    $"Process {endProcess.Identity.ProcessId} processor time decreased for the same stable process instance.");
            }
            matched.Add(new MatchedProcess(
                endProcess.Identity,
                endProcess.TotalProcessorTime - startProcess.TotalProcessorTime,
                endProcess.WorkingSetBytes,
                endProcess.PrivateMemoryBytes,
                endProcess.ThreadCount,
                endProcess.Identity.ProcessId == captureProcessId));
        }

        var ordered = matched
            .OrderByDescending(static process => process.ProcessorTimeDelta)
            .ThenByDescending(static process => process.WorkingSetBytes)
            .ThenByDescending(static process => process.PrivateMemoryBytes)
            .ThenBy(static process => process.Identity.ProcessId)
            .ThenBy(static process => process.Identity.StartedAt)
            .ToArray();
        var visible = ordered.Take(budget.MaxRows).ToArray();
        var hidden = ordered.Skip(budget.MaxRows).ToArray();
        var rows = visible
            .Select(static process => new MachineProcessActivityRow(
                process.Identity,
                process.ProcessorTimeDelta,
                process.WorkingSetBytes,
                process.PrivateMemoryBytes,
                process.ThreadCount,
                process.IsCaptureProcess))
            .ToArray();

        var capture = ordered.FirstOrDefault(static process => process.IsCaptureProcess);
        var startKeys = startByKey.Keys.ToHashSet();
        var endKeys = endByKey.Keys.ToHashSet();
        return new MachineProcessActivityReport(
            budget,
            start.CapturedAt,
            end.CapturedAt,
            providerOverheadDuration,
            start.EnumeratedProcessCount,
            end.EnumeratedProcessCount,
            start.InaccessibleProcessCount,
            end.InaccessibleProcessCount,
            start.SnapshotCapReached || end.SnapshotCapReached,
            ordered.Length,
            endKeys.Count(key => !startKeys.Contains(key)),
            startKeys.Count(key => !endKeys.Contains(key)),
            rows,
            hidden.Length,
            SumProcessorTime(hidden.Select(static process => process.ProcessorTimeDelta)),
            capture?.ProcessorTimeDelta,
            capture?.WorkingSetBytes);
    }

    private static TimeSpan SumProcessorTime(IEnumerable<TimeSpan> durations)
    {
        long ticks = 0;
        foreach (var duration in durations)
        {
            var remaining = long.MaxValue - ticks;
            ticks += duration.Ticks > remaining ? remaining : duration.Ticks;
        }
        return TimeSpan.FromTicks(ticks);
    }

    private sealed record MatchedProcess(
        MachineProcessIdentity Identity,
        TimeSpan ProcessorTimeDelta,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        int ThreadCount,
        bool IsCaptureProcess);
}

public interface IMachineProcessActivityProvider
{
    ValueTask<MachineProcessActivityResult> CaptureAsync(
        MachineProcessActivityBudget budget,
        CancellationToken cancellationToken = default);
}

internal readonly record struct ProcessKey(int ProcessId, long StartedUtcTicks)
{
    public static ProcessKey From(MachineProcessIdentity identity) =>
        new(identity.ProcessId, identity.StartedAt.UtcDateTime.Ticks);
}
