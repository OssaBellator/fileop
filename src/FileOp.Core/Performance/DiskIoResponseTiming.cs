namespace FileOp.Core.Performance;

public sealed record DiskIoResponseTimingObservation(
    DateTimeOffset Timestamp,
    uint PhysicalDiskNumber,
    DiskIoOperationKind Operation,
    TimeSpan ResponseTime)
{
    public DiskIoProcessIdentity? Owner { get; init; }
}

public sealed record DiskIoResponseTimingSummary
{
    public DiskIoResponseTimingSummary(
        int sampleCount,
        TimeSpan minimum,
        TimeSpan median,
        TimeSpan? p95,
        TimeSpan maximum)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCount),
                sampleCount,
                "Disk-I/O response timing summaries require at least one sample.");
        }
        if (minimum < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimum),
                minimum,
                "Disk-I/O response timing minimum cannot be negative.");
        }
        if (median < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(median),
                median,
                "Disk-I/O response timing median cannot be negative.");
        }
        if (p95 is { } percentile && percentile < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(p95),
                p95,
                "Disk-I/O response timing p95 cannot be negative.");
        }
        if (maximum < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximum),
                maximum,
                "Disk-I/O response timing maximum cannot be negative.");
        }
        if (minimum > median || median > maximum)
        {
            throw new ArgumentException(
                "Disk-I/O response timing summaries require minimum <= median <= maximum.");
        }
        if (sampleCount < DiskIoResponseTimingAnalyzer.MinimumSamplesForP95 && p95 is not null)
        {
            throw new ArgumentException(
                $"Disk-I/O response timing p95 requires at least {DiskIoResponseTimingAnalyzer.MinimumSamplesForP95} samples.",
                nameof(p95));
        }
        if (sampleCount >= DiskIoResponseTimingAnalyzer.MinimumSamplesForP95 && p95 is null)
        {
            throw new ArgumentException(
                $"Disk-I/O response timing summaries with {DiskIoResponseTimingAnalyzer.MinimumSamplesForP95}+ samples require p95 evidence.",
                nameof(p95));
        }
        if (p95 is { } percentileValue &&
            (percentileValue < median || percentileValue > maximum))
        {
            throw new ArgumentException(
                "Disk-I/O response timing p95 must be between the median and maximum.",
                nameof(p95));
        }

        SampleCount = sampleCount;
        Minimum = minimum;
        Median = median;
        P95 = p95;
        Maximum = maximum;
    }

    public int SampleCount { get; }
    public TimeSpan Minimum { get; }
    public TimeSpan Median { get; }
    public TimeSpan? P95 { get; }
    public TimeSpan Maximum { get; }
}

public sealed record DiskIoDiskResponseTiming
{
    public DiskIoDiskResponseTiming(
        uint physicalDiskNumber,
        DiskIoResponseTimingSummary? reads,
        DiskIoResponseTimingSummary? writes,
        DiskIoResponseTimingSummary? flushes)
    {
        if (reads is null && writes is null && flushes is null)
        {
            throw new ArgumentException(
                "A physical-disk response timing row requires at least one operation summary.");
        }

        PhysicalDiskNumber = physicalDiskNumber;
        Reads = reads;
        Writes = writes;
        Flushes = flushes;
    }

    public uint PhysicalDiskNumber { get; }
    public DiskIoResponseTimingSummary? Reads { get; }
    public DiskIoResponseTimingSummary? Writes { get; }
    public DiskIoResponseTimingSummary? Flushes { get; }

    public long SampleCount =>
        (long)(Reads?.SampleCount ?? 0) +
        (Writes?.SampleCount ?? 0) +
        (Flushes?.SampleCount ?? 0);
}

public static class DiskIoResponseTimingAnalyzer
{
    public const int MinimumSamplesForP95 = 5;

    public static IReadOnlyList<DiskIoDiskResponseTiming> Analyze(
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<DiskIoResponseTimingObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (endedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endedAt),
                endedAt,
                "Disk-I/O response-timing end time cannot precede the start time.");
        }

        foreach (var observation in observations)
        {
            ValidateObservation(observation, startedAt, endedAt);
        }

        return observations
            .GroupBy(static observation => observation.PhysicalDiskNumber)
            .OrderBy(static group => group.Key)
            .Select(group => new DiskIoDiskResponseTiming(
                group.Key,
                Build(group, DiskIoOperationKind.Read),
                Build(group, DiskIoOperationKind.Write),
                Build(group, DiskIoOperationKind.Flush)))
            .ToArray();
    }

    private static DiskIoResponseTimingSummary? Build(
        IEnumerable<DiskIoResponseTimingObservation> observations,
        DiskIoOperationKind operation)
    {
        var ticks = observations
            .Where(observation => observation.Operation == operation)
            .Select(static observation => observation.ResponseTime.Ticks)
            .OrderBy(static value => value)
            .ToArray();
        if (ticks.Length == 0)
        {
            return null;
        }

        var middle = ticks.Length / 2;
        var medianTicks = ticks.Length % 2 == 1
            ? ticks[middle]
            : Midpoint(ticks[middle - 1], ticks[middle]);
        TimeSpan? p95 = null;
        if (ticks.Length >= MinimumSamplesForP95)
        {
            var rank = ((long)ticks.Length * 95 + 99) / 100;
            p95 = TimeSpan.FromTicks(ticks[checked((int)rank - 1)]);
        }

        return new DiskIoResponseTimingSummary(
            ticks.Length,
            TimeSpan.FromTicks(ticks[0]),
            TimeSpan.FromTicks(medianTicks),
            p95,
            TimeSpan.FromTicks(ticks[^1]));
    }

    private static void ValidateObservation(
        DiskIoResponseTimingObservation observation,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        if (observation.Timestamp < startedAt || observation.Timestamp > endedAt)
        {
            throw new InvalidDataException(
                $"Disk-I/O response timing at {observation.Timestamp:O} is outside the declared capture window {startedAt:O}–{endedAt:O}.");
        }
        if (!Enum.IsDefined(observation.Operation))
        {
            throw new InvalidDataException(
                $"Disk-I/O response timing has unsupported operation value {(int)observation.Operation}.");
        }
        if (observation.ResponseTime < TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"Disk-I/O response timing cannot be negative ({observation.ResponseTime}).");
        }
        if (observation.Owner is { } owner)
        {
            if (owner.ProcessId <= 0)
            {
                throw new InvalidDataException(
                    $"Disk-I/O response timing owner process ID {owner.ProcessId} is invalid.");
            }
            if (owner.StartedAt is { } processStart && processStart > observation.Timestamp)
            {
                throw new InvalidDataException(
                    $"Disk-I/O response timing owner process {owner.ProcessId} starts after its completion timestamp.");
            }
            if (owner.ImageName is { Length: > 0 } imageName && string.IsNullOrWhiteSpace(imageName))
            {
                throw new InvalidDataException(
                    "Disk-I/O response timing owner image name cannot contain only whitespace.");
            }
        }
    }

    private static long Midpoint(long lower, long upper) =>
        lower + ((upper - lower) / 2);
}
