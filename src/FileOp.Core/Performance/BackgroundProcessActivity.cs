namespace FileOp.Core.Performance;

public sealed record BackgroundProcessActivityBudget
{
    public static readonly TimeSpan DefaultSamplingDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MinimumSamplingDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumSamplingDelay = TimeSpan.FromSeconds(3);

    public const int DefaultMaxProcessSnapshots = 1024;
    public const int MaximumProcessSnapshots = 4096;
    public const int DefaultMaxRows = 20;
    public const int MaximumRows = 100;

    public static BackgroundProcessActivityBudget Default { get; } = new(
        DefaultSamplingDelay,
        DefaultMaxProcessSnapshots,
        DefaultMaxRows);

    public BackgroundProcessActivityBudget(
        TimeSpan samplingDelay,
        int maxProcessSnapshots,
        int maxRows)
    {
        if (samplingDelay < MinimumSamplingDelay || samplingDelay > MaximumSamplingDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplingDelay),
                samplingDelay,
                $"Background-process sampling delay must be between {MinimumSamplingDelay.TotalMilliseconds:N0} ms and {MaximumSamplingDelay.TotalSeconds:N0} s.");
        }
        if (maxProcessSnapshots <= 0 || maxProcessSnapshots > MaximumProcessSnapshots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxProcessSnapshots),
                maxProcessSnapshots,
                $"Background-process snapshot limit must be between 1 and {MaximumProcessSnapshots:N0} process instances.");
        }
        if (maxRows <= 0 || maxRows > MaximumRows || maxRows > maxProcessSnapshots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRows),
                maxRows,
                $"Background-process visible rows must be between 1 and {Math.Min(MaximumRows, maxProcessSnapshots):N0}.");
        }

        SamplingDelay = samplingDelay;
        MaxProcessSnapshots = maxProcessSnapshots;
        MaxRows = maxRows;
    }

    public TimeSpan SamplingDelay { get; }
    public int MaxProcessSnapshots { get; }
    public int MaxRows { get; }
}

public sealed record BackgroundProcessIdentity
{
    public BackgroundProcessIdentity(
        int processId,
        DateTimeOffset startedAt,
        string? imageName)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                processId,
                "A background-process identity requires a positive process ID.");
        }
        if (imageName is { Length: > 0 } && string.IsNullOrWhiteSpace(imageName))
        {
            throw new ArgumentException(
                "A background-process image name cannot contain only whitespace.",
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

public sealed record BackgroundProcessCounterSnapshot
{
    public BackgroundProcessCounterSnapshot(
        BackgroundProcessIdentity identity,
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
                "Background-process total processor time cannot be negative.");
        }
        if (workingSetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingSetBytes),
                workingSetBytes,
                "Background-process working set cannot be negative.");
        }
        if (privateMemoryBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(privateMemoryBytes),
                privateMemoryBytes,
                "Background-process private memory cannot be negative.");
        }
        if (threadCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threadCount),
                threadCount,
                "Background-process thread count cannot be negative.");
        }

        Identity = identity;
        TotalProcessorTime = totalProcessorTime;
        WorkingSetBytes = workingSetBytes;
        PrivateMemoryBytes = privateMemoryBytes;
        ThreadCount = threadCount;
    }

    public BackgroundProcessIdentity Identity { get; }
    public TimeSpan TotalProcessorTime { get; }
    public long WorkingSetBytes { get; }
    public long PrivateMemoryBytes { get; }
    public int ThreadCount { get; }
}

public sealed record BackgroundProcessActivityFrame
{
    public BackgroundProcessActivityFrame(
        DateTimeOffset capturedAt,
        int enumeratedProcessCount,
        int inaccessibleProcessCount,
        bool snapshotCapReached,
        IReadOnlyList<BackgroundProcessCounterSnapshot> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        if (enumeratedProcessCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(enumeratedProcessCount),
                enumeratedProcessCount,
                "Enumerated process count cannot be negative.");
        }
        if (inaccessibleProcessCount < 0 || inaccessibleProcessCount > enumeratedProcessCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inaccessibleProcessCount),
                inaccessibleProcessCount,
                "Inaccessible process count must be between zero and the enumerated process count.");
        }
        if (processes.Count > enumeratedProcessCount)
        {
            throw new ArgumentException(
                "Captured process counters cannot exceed the enumerated process count.",
                nameof(processes));
        }

        var snapshot = processes.ToArray();
        var identities = new HashSet<ProcessKey>();
        foreach (var process in snapshot)
        {
            ArgumentNullException.ThrowIfNull(process);
            if (process.Identity.StartedAt > capturedAt.ToUniversalTime())
            {
                throw new ArgumentException(
                    $"Process {process.Identity.ProcessId} starts after the frame capture timestamp.",
                    nameof(processes));
            }
            if (!identities.Add(ProcessKey.From(process.Identity)))
            {
                throw new ArgumentException(
                    "A background-process frame cannot repeat the same stable process instance.",
                    nameof(processes));
            }
        }

        CapturedAt = capturedAt.ToUniversalTime();
        EnumeratedProcessCount = enumeratedProcessCount;
        InaccessibleProcessCount = inaccessibleProcessCount;
        SnapshotCapReached = snapshotCapReached;
        Processes = snapshot;
    }

    public DateTimeOffset CapturedAt { get; }
    public int EnumeratedProcessCount { get; }
    public int InaccessibleProcessCount { get; }
    public bool SnapshotCapReached { get; }
    public IReadOnlyList<BackgroundProcessCounterSnapshot> Processes { get; }

    private readonly record struct ProcessKey(int ProcessId, long StartedUtcTicks)
    {
        public static ProcessKey From(BackgroundProcessIdentity identity) =>
            new(identity.ProcessId, identity.StartedAt.UtcDateTime.Ticks);
    }
}

public sealed record BackgroundProcessActivityRow
{
    public BackgroundProcessActivityRow(
        BackgroundProcessIdentity identity,
        TimeSpan processorTimeDelta,
        long workingSetBytes,
        long privateMemoryBytes,
        int threadCount,
        bool isCaptureProcess)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (processorTimeDelta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processorTimeDelta),
                processorTimeDelta,
                "Background-process processor-time delta cannot be negative.");
        }
        if (workingSetBytes < 0 || privateMemoryBytes < 0 || threadCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workingSetBytes),
                "Background-process end-of-sample resource counters cannot be negative.");
        }

        Identity = identity;
        ProcessorTimeDelta = processorTimeDelta;
        WorkingSetBytes = workingSetBytes;
        PrivateMemoryBytes = privateMemoryBytes;
        ThreadCount = threadCount;
        IsCaptureProcess = isCaptureProcess;
    }

    public BackgroundProcessIdentity Identity { get; }
    public TimeSpan ProcessorTimeDelta { get; }
    public long WorkingSetBytes { get; }
    public long PrivateMemoryBytes { get; }
    public int ThreadCount { get; }
    public bool IsCaptureProcess { get; }
}

public sealed record BackgroundProcessActivityReport
{
    public BackgroundProcessActivityReport(
        BackgroundProcessActivityBudget budget,
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
        IReadOnlyList<BackgroundProcessActivityRow> rows,
        int otherMatchedProcessCount,
        TimeSpan otherMatchedProcessorTime,
        TimeSpan? captureProcessProcessorTime,
        long? captureProcessWorkingSetBytes)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(rows);
        if (endFrameCapturedAt < startFrameCapturedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endFrameCapturedAt),
                endFrameCapturedAt,
                "Background-process end frame cannot precede the start frame.");
        }
        if (providerOverheadDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerOverheadDuration),
                providerOverheadDuration,
                "Background-process provider overhead cannot be negative.");
        }
        foreach (var count in new[]
        {
            startEnumeratedProcessCount,
            endEnumeratedProcessCount,
            startInaccessibleProcessCount,
            endInaccessibleProcessCount,
            stableMatchedProcessCount,
            startedDuringSampleCount,
            exitedDuringSampleCount,
            otherMatchedProcessCount,
        })
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stableMatchedProcessCount),
                    "Background-process evidence counts cannot be negative.");
            }
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
                "Visible background-process rows exceed the capture budget or stable matched-process count.",
                nameof(rows));
        }
        if (otherMatchedProcessCount != stableMatchedProcessCount - rows.Count)
        {
            throw new ArgumentException(
                "Other matched-process count must account for every stable matched process hidden by the visible-row budget.",
                nameof(otherMatchedProcessCount));
        }
        if (otherMatchedProcessorTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(otherMatchedProcessorTime),
                otherMatchedProcessorTime,
                "Other matched-process processor time cannot be negative.");
        }
        if (captureProcessProcessorTime is { } captureCpu && captureCpu < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureProcessProcessorTime),
                captureProcessProcessorTime,
                "Capture-process processor time cannot be negative.");
        }
        if (captureProcessWorkingSetBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureProcessWorkingSetBytes),
                captureProcessWorkingSetBytes,
                "Capture-process working set cannot be negative.");
        }
        if ((captureProcessProcessorTime is null) != (captureProcessWorkingSetBytes is null))
        {
            throw new ArgumentException(
                "Capture-process CPU and working-set evidence must be available or unavailable together.");
        }

        var rowSnapshot = rows.ToArray();
        var identities = new HashSet<ProcessKey>();
        foreach (var row in rowSnapshot)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (!identities.Add(ProcessKey.From(row.Identity)))
            {
                throw new ArgumentException(
                    "Visible background-process rows cannot repeat a stable process instance.",
                    nameof(rows));
            }
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

    public BackgroundProcessActivityBudget Budget { get; }
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
    public IReadOnlyList<BackgroundProcessActivityRow> Rows { get; }
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
        SumProcessorTime(new[] { VisibleProcessorTime, OtherMatchedProcessorTime });

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

    private readonly record struct ProcessKey(int ProcessId, long StartedUtcTicks)
    {
        public static ProcessKey From(BackgroundProcessIdentity identity) =>
            new(identity.ProcessId, identity.StartedAt.UtcDateTime.Ticks);
    }
}

public enum BackgroundProcessActivityStatus
{
    Completed,
    Unsupported,
    Unavailable,
}

public sealed record BackgroundProcessActivityResult
{
    private BackgroundProcessActivityResult(
        BackgroundProcessActivityBudget budget,
        BackgroundProcessActivityStatus status,
        BackgroundProcessActivityReport? report,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unsupported background-process activity status.");
        }
        if ((status == BackgroundProcessActivityStatus.Completed) != (report is not null))
        {
            throw new ArgumentException(
                "Completed background-process results require a report; unavailable results cannot carry one.",
                nameof(report));
        }
        if (report is not null && report.Budget != budget)
        {
            throw new ArgumentException(
                "Background-process report budget must match the result budget.",
                nameof(report));
        }

        Budget = budget;
        Status = status;
        Report = report;
        Detail = detail;
    }

    public BackgroundProcessActivityBudget Budget { get; }
    public BackgroundProcessActivityStatus Status { get; }
    public BackgroundProcessActivityReport? Report { get; }
    public string Detail { get; }

    public static BackgroundProcessActivityResult Completed(
        BackgroundProcessActivityBudget budget,
        BackgroundProcessActivityReport report,
        string detail) =>
        new(budget, BackgroundProcessActivityStatus.Completed, report, detail);

    public static BackgroundProcessActivityResult Unavailable(
        BackgroundProcessActivityBudget budget,
        BackgroundProcessActivityStatus status,
        string detail)
    {
        if (status == BackgroundProcessActivityStatus.Completed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Completed background-process results must use the Completed factory.");
        }
        return new BackgroundProcessActivityResult(budget, status, report: null, detail);
    }
}

public static class BackgroundProcessActivityAnalyzer
{
    public static BackgroundProcessActivityReport Analyze(
        BackgroundProcessActivityBudget budget,
        BackgroundProcessActivityFrame start,
        BackgroundProcessActivityFrame end,
        int captureProcessId,
        TimeSpan providerOverheadDuration)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        if (captureProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(captureProcessId),
                captureProcessId,
                "Capture process ID must be positive.");
        }
        if (end.CapturedAt < start.CapturedAt)
        {
            throw new InvalidDataException(
                "Background-process end-frame timestamp precedes the start frame.");
        }
        if (providerOverheadDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerOverheadDuration),
                providerOverheadDuration,
                "Provider overhead duration cannot be negative.");
        }
        if (start.Processes.Count > budget.MaxProcessSnapshots ||
            end.Processes.Count > budget.MaxProcessSnapshots)
        {
            throw new InvalidDataException(
                "Background-process frame exceeds the requested snapshot budget.");
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
            .Select(static process => new BackgroundProcessActivityRow(
                process.Identity,
                process.ProcessorTimeDelta,
                process.WorkingSetBytes,
                process.PrivateMemoryBytes,
                process.ThreadCount,
                process.IsCaptureProcess))
            .ToArray();

        var captureProcess = ordered.FirstOrDefault(static process => process.IsCaptureProcess);
        var startKeys = startByKey.Keys.ToHashSet();
        var endKeys = endByKey.Keys.ToHashSet();
        var startedCount = endKeys.Count(key => !startKeys.Contains(key));
        var exitedCount = startKeys.Count(key => !endKeys.Contains(key));

        return new BackgroundProcessActivityReport(
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
            startedCount,
            exitedCount,
            rows,
            hidden.Length,
            SumProcessorTime(hidden.Select(static process => process.ProcessorTimeDelta)),
            captureProcess?.ProcessorTimeDelta,
            captureProcess?.WorkingSetBytes);
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
        BackgroundProcessIdentity Identity,
        TimeSpan ProcessorTimeDelta,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        int ThreadCount,
        bool IsCaptureProcess);

    private readonly record struct ProcessKey(int ProcessId, long StartedUtcTicks)
    {
        public static ProcessKey From(BackgroundProcessIdentity identity) =>
            new(identity.ProcessId, identity.StartedAt.UtcDateTime.Ticks);
    }
}

public interface IBackgroundProcessActivityProvider
{
    ValueTask<BackgroundProcessActivityResult> CaptureAsync(
        BackgroundProcessActivityBudget budget,
        CancellationToken cancellationToken = default);
}
