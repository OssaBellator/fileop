namespace FileOp.Core.Performance;

public sealed record DiskIoProcessResponseTiming
{
    public DiskIoProcessResponseTiming(
        DiskIoProcessIdentity owner,
        DiskIoResponseTimingSummary? reads,
        DiskIoResponseTimingSummary? writes,
        DiskIoResponseTimingSummary? flushes)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(owner),
                owner.ProcessId,
                "Disk-I/O process timing owner must have a positive process ID.");
        }
        if (reads is null && writes is null && flushes is null)
        {
            throw new ArgumentException(
                "A process response-timing row requires at least one operation summary.",
                nameof(reads));
        }

        Owner = owner;
        Reads = reads;
        Writes = writes;
        Flushes = flushes;
    }

    public DiskIoProcessIdentity Owner { get; }
    public DiskIoResponseTimingSummary? Reads { get; }
    public DiskIoResponseTimingSummary? Writes { get; }
    public DiskIoResponseTimingSummary? Flushes { get; }

    public long SampleCount =>
        (long)(Reads?.SampleCount ?? 0) +
        (Writes?.SampleCount ?? 0) +
        (Flushes?.SampleCount ?? 0);
}

public sealed record DiskIoDiskProcessResponseTiming
{
    public DiskIoDiskProcessResponseTiming(
        uint physicalDiskNumber,
        IReadOnlyList<DiskIoProcessResponseTiming> owners,
        long unattributedReadSamples,
        long unattributedWriteSamples,
        long unattributedFlushSamples,
        long otherIdentifiedReadSamples,
        long otherIdentifiedWriteSamples,
        long otherIdentifiedFlushSamples)
    {
        ArgumentNullException.ThrowIfNull(owners);
        if (unattributedReadSamples < 0 ||
            unattributedWriteSamples < 0 ||
            unattributedFlushSamples < 0 ||
            otherIdentifiedReadSamples < 0 ||
            otherIdentifiedWriteSamples < 0 ||
            otherIdentifiedFlushSamples < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unattributedReadSamples),
                "Disk-I/O process timing sample counts cannot be negative.");
        }

        var snapshot = owners.ToArray();
        var identities = new HashSet<ProcessKey>();
        foreach (var owner in snapshot)
        {
            ArgumentNullException.ThrowIfNull(owner);
            if (!identities.Add(ProcessKey.From(owner.Owner)))
            {
                throw new ArgumentException(
                    "Disk-I/O process timing rows cannot repeat the same process instance.",
                    nameof(owners));
            }
        }

        PhysicalDiskNumber = physicalDiskNumber;
        Owners = snapshot;
        UnattributedReadSamples = unattributedReadSamples;
        UnattributedWriteSamples = unattributedWriteSamples;
        UnattributedFlushSamples = unattributedFlushSamples;
        OtherIdentifiedReadSamples = otherIdentifiedReadSamples;
        OtherIdentifiedWriteSamples = otherIdentifiedWriteSamples;
        OtherIdentifiedFlushSamples = otherIdentifiedFlushSamples;
    }

    public uint PhysicalDiskNumber { get; }
    public IReadOnlyList<DiskIoProcessResponseTiming> Owners { get; }
    public long UnattributedReadSamples { get; }
    public long UnattributedWriteSamples { get; }
    public long UnattributedFlushSamples { get; }
    public long OtherIdentifiedReadSamples { get; }
    public long OtherIdentifiedWriteSamples { get; }
    public long OtherIdentifiedFlushSamples { get; }

    public long VisibleOwnerSamples => Owners.Sum(static owner => owner.SampleCount);
    public long UnattributedSamples =>
        UnattributedReadSamples + UnattributedWriteSamples + UnattributedFlushSamples;
    public long OtherIdentifiedSamples =>
        OtherIdentifiedReadSamples + OtherIdentifiedWriteSamples + OtherIdentifiedFlushSamples;
    public long TotalSamples => VisibleOwnerSamples + UnattributedSamples + OtherIdentifiedSamples;

    private readonly record struct ProcessKey(int ProcessId, DateTimeOffset? StartedAt)
    {
        public static ProcessKey From(DiskIoProcessIdentity owner) =>
            new(owner.ProcessId, owner.StartedAt);
    }
}

public static class DiskIoProcessResponseTimingAnalyzer
{
    public static IReadOnlyList<DiskIoDiskProcessResponseTiming> Analyze(
        DiskIoAttributionReport attribution,
        IReadOnlyList<DiskIoResponseTimingObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count != attribution.AcceptedEventCount)
        {
            throw new InvalidDataException(
                $"Process response-timing input has {observations.Count:N0} samples but the attribution report has {attribution.AcceptedEventCount:N0} accepted events.");
        }

        foreach (var observation in observations)
        {
            DiskIoResponseTimingAnalyzer.ValidateObservation(
                observation,
                attribution.StartedAt,
                attribution.EndedAt);
        }

        var byDisk = observations
            .GroupBy(static observation => observation.PhysicalDiskNumber)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        if (byDisk.Count != attribution.Disks.Count)
        {
            throw new InvalidDataException(
                "Process response-timing disk scope does not match the attribution report.");
        }

        var result = new List<DiskIoDiskProcessResponseTiming>(attribution.Disks.Count);
        foreach (var disk in attribution.Disks)
        {
            if (!byDisk.TryGetValue(disk.PhysicalDiskNumber, out var diskObservations))
            {
                throw new InvalidDataException(
                    $"Process response timing is missing physical disk {disk.PhysicalDiskNumber} from the attribution report.");
            }

            ValidateDiskOperationCounts(disk, diskObservations);
            var visibleKeys = disk.Owners
                .Select(static owner => ProcessKey.From(owner.Owner))
                .ToHashSet();
            if (visibleKeys.Count != disk.Owners.Count)
            {
                throw new InvalidDataException(
                    $"Attribution report for physical disk {disk.PhysicalDiskNumber} repeats a visible process instance.");
            }

            var visibleRows = new List<DiskIoProcessResponseTiming>(disk.Owners.Count);
            foreach (var attributionOwner in disk.Owners)
            {
                var key = ProcessKey.From(attributionOwner.Owner);
                var samples = diskObservations
                    .Where(observation => observation.Owner is { } owner && ProcessKey.From(owner) == key)
                    .ToArray();
                ValidateOwnerOperationCounts(disk.PhysicalDiskNumber, attributionOwner, samples);
                visibleRows.Add(new DiskIoProcessResponseTiming(
                    attributionOwner.Owner,
                    DiskIoResponseTimingAnalyzer.BuildSummary(samples, DiskIoOperationKind.Read),
                    DiskIoResponseTimingAnalyzer.BuildSummary(samples, DiskIoOperationKind.Write),
                    DiskIoResponseTimingAnalyzer.BuildSummary(samples, DiskIoOperationKind.Flush)));
            }

            var unattributedRead = Count(diskObservations, DiskIoOperationKind.Read, ownerExpected: false);
            var unattributedWrite = Count(diskObservations, DiskIoOperationKind.Write, ownerExpected: false);
            var unattributedFlush = Count(diskObservations, DiskIoOperationKind.Flush, ownerExpected: false);
            if (unattributedRead != disk.UnattributedReadOperations ||
                unattributedWrite != disk.UnattributedWriteOperations ||
                unattributedFlush != disk.UnattributedFlushOperations)
            {
                throw new InvalidDataException(
                    $"Physical disk {disk.PhysicalDiskNumber} unattributed timing counts do not match attribution evidence.");
            }

            var hidden = diskObservations
                .Where(observation =>
                    observation.Owner is { } owner &&
                    !visibleKeys.Contains(ProcessKey.From(owner)))
                .ToArray();
            var hiddenRead = Count(hidden, DiskIoOperationKind.Read);
            var hiddenWrite = Count(hidden, DiskIoOperationKind.Write);
            var hiddenFlush = Count(hidden, DiskIoOperationKind.Flush);
            if (hiddenRead != disk.OtherIdentifiedReadOperations ||
                hiddenWrite != disk.OtherIdentifiedWriteOperations ||
                hiddenFlush != disk.OtherIdentifiedFlushOperations)
            {
                throw new InvalidDataException(
                    $"Physical disk {disk.PhysicalDiskNumber} hidden identified timing counts do not match attribution evidence.");
            }

            var row = new DiskIoDiskProcessResponseTiming(
                disk.PhysicalDiskNumber,
                visibleRows,
                unattributedRead,
                unattributedWrite,
                unattributedFlush,
                hiddenRead,
                hiddenWrite,
                hiddenFlush);
            if (row.TotalSamples != disk.TotalOperations)
            {
                throw new InvalidDataException(
                    $"Physical disk {disk.PhysicalDiskNumber} process timing total {row.TotalSamples:N0} does not match {disk.TotalOperations:N0} attributed operations.");
            }
            result.Add(row);
        }

        return result.ToArray();
    }

    private static void ValidateDiskOperationCounts(
        DiskIoDiskAttribution disk,
        IReadOnlyList<DiskIoResponseTimingObservation> observations)
    {
        var reads = Count(observations, DiskIoOperationKind.Read);
        var writes = Count(observations, DiskIoOperationKind.Write);
        var flushes = Count(observations, DiskIoOperationKind.Flush);
        if (reads != disk.ReadOperations ||
            writes != disk.WriteOperations ||
            flushes != disk.FlushOperations)
        {
            throw new InvalidDataException(
                $"Physical disk {disk.PhysicalDiskNumber} timing operation counts do not match attribution evidence.");
        }
    }

    private static void ValidateOwnerOperationCounts(
        uint physicalDiskNumber,
        DiskIoProcessAttribution attribution,
        IReadOnlyList<DiskIoResponseTimingObservation> observations)
    {
        var reads = Count(observations, DiskIoOperationKind.Read);
        var writes = Count(observations, DiskIoOperationKind.Write);
        var flushes = Count(observations, DiskIoOperationKind.Flush);
        if (reads != attribution.ReadOperations ||
            writes != attribution.WriteOperations ||
            flushes != attribution.FlushOperations)
        {
            throw new InvalidDataException(
                $"Physical disk {physicalDiskNumber} timing counts for process {attribution.Owner.ProcessId} do not match its visible attribution row.");
        }
    }

    private static long Count(
        IEnumerable<DiskIoResponseTimingObservation> observations,
        DiskIoOperationKind operation,
        bool? ownerExpected = null) =>
        observations.LongCount(observation =>
            observation.Operation == operation &&
            (ownerExpected is null || observation.Owner is not null == ownerExpected));

    private readonly record struct ProcessKey(int ProcessId, DateTimeOffset? StartedAt)
    {
        public static ProcessKey From(DiskIoProcessIdentity owner) =>
            new(owner.ProcessId, owner.StartedAt);
    }
}
