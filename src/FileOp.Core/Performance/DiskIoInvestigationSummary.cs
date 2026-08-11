namespace FileOp.Core.Performance;

public sealed record DiskIoObservedP95Cue
{
    public DiskIoObservedP95Cue(
        uint physicalDiskNumber,
        DiskIoOperationKind operation,
        DiskIoResponseTimingSummary timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unsupported Disk-I/O operation for an observed p95 cue.");
        }
        if (timing.P95 is null)
        {
            throw new ArgumentException(
                "An observed p95 cue requires timing evidence with an eligible p95.",
                nameof(timing));
        }

        PhysicalDiskNumber = physicalDiskNumber;
        Operation = operation;
        Timing = timing;
    }

    public uint PhysicalDiskNumber { get; }
    public DiskIoOperationKind Operation { get; }
    public DiskIoResponseTimingSummary Timing { get; }
    public TimeSpan P95 => Timing.P95!.Value;
}

public sealed record DiskIoObservedByteDiskCue
{
    public DiskIoObservedByteDiskCue(
        uint physicalDiskNumber,
        long totalBytes,
        long totalOperations,
        long unattributedBytes,
        double? attributionCoveragePercent)
    {
        if (totalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalBytes),
                totalBytes,
                "An observed byte-volume disk cue requires positive transferred bytes.");
        }
        if (totalOperations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalOperations),
                totalOperations,
                "An observed byte-volume disk cue requires at least one operation.");
        }
        if (unattributedBytes < 0 || unattributedBytes > totalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(unattributedBytes),
                unattributedBytes,
                "Unattributed bytes must be between zero and the observed disk byte total.");
        }
        if (attributionCoveragePercent is { } coverage &&
            (!double.IsFinite(coverage) || coverage < 0d || coverage > 100d))
        {
            throw new ArgumentOutOfRangeException(
                nameof(attributionCoveragePercent),
                attributionCoveragePercent,
                "Attribution coverage must be finite and between 0 and 100 percent.");
        }

        PhysicalDiskNumber = physicalDiskNumber;
        TotalBytes = totalBytes;
        TotalOperations = totalOperations;
        UnattributedBytes = unattributedBytes;
        AttributionCoveragePercent = attributionCoveragePercent;
    }

    public uint PhysicalDiskNumber { get; }
    public long TotalBytes { get; }
    public long TotalOperations { get; }
    public long UnattributedBytes { get; }
    public double? AttributionCoveragePercent { get; }
}

public sealed record DiskIoObservedOwnerBytesCue
{
    public DiskIoObservedOwnerBytesCue(
        uint physicalDiskNumber,
        DiskIoProcessIdentity owner,
        long totalBytes,
        long totalOperations,
        double observedByteSharePercent)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(owner),
                owner.ProcessId,
                "An observed owner cue requires a positive process ID.");
        }
        if (totalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalBytes),
                totalBytes,
                "An observed owner byte cue requires positive transferred bytes.");
        }
        if (totalOperations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalOperations),
                totalOperations,
                "An observed owner byte cue requires at least one operation.");
        }
        if (!double.IsFinite(observedByteSharePercent) ||
            observedByteSharePercent < 0d ||
            observedByteSharePercent > 100d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedByteSharePercent),
                observedByteSharePercent,
                "Observed owner byte share must be finite and between 0 and 100 percent.");
        }

        PhysicalDiskNumber = physicalDiskNumber;
        Owner = owner;
        TotalBytes = totalBytes;
        TotalOperations = totalOperations;
        ObservedByteSharePercent = observedByteSharePercent;
    }

    public uint PhysicalDiskNumber { get; }
    public DiskIoProcessIdentity Owner { get; }
    public long TotalBytes { get; }
    public long TotalOperations { get; }
    public double ObservedByteSharePercent { get; }
}

public sealed record DiskIoInvestigationSummary
{
    public DiskIoInvestigationSummary(
        int acceptedEventCount,
        bool evidenceMayBeIncomplete,
        DiskIoObservedP95Cue? highestObservedP95,
        DiskIoObservedByteDiskCue? largestObservedByteDisk,
        DiskIoObservedOwnerBytesCue? largestIdentifiedOwner)
    {
        if (acceptedEventCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acceptedEventCount),
                acceptedEventCount,
                "Investigation evidence cannot have a negative accepted-event count.");
        }
        if (acceptedEventCount == 0 &&
            (highestObservedP95 is not null ||
             largestObservedByteDisk is not null ||
             largestIdentifiedOwner is not null))
        {
            throw new ArgumentException(
                "An empty Disk-I/O capture cannot carry comparative investigation cues.");
        }

        AcceptedEventCount = acceptedEventCount;
        EvidenceMayBeIncomplete = evidenceMayBeIncomplete;
        HighestObservedP95 = highestObservedP95;
        LargestObservedByteDisk = largestObservedByteDisk;
        LargestIdentifiedOwner = largestIdentifiedOwner;
    }

    public int AcceptedEventCount { get; }
    public bool EvidenceMayBeIncomplete { get; }
    public DiskIoObservedP95Cue? HighestObservedP95 { get; }
    public DiskIoObservedByteDiskCue? LargestObservedByteDisk { get; }
    public DiskIoObservedOwnerBytesCue? LargestIdentifiedOwner { get; }

    public bool HasComparativeCue =>
        HighestObservedP95 is not null ||
        LargestObservedByteDisk is not null ||
        LargestIdentifiedOwner is not null;
}

public static class DiskIoInvestigationSummaryAnalyzer
{
    public static DiskIoInvestigationSummary Analyze(DiskIoCaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != DiskIoCaptureStatus.Completed || result.Report is null)
        {
            throw new InvalidOperationException(
                "Disk-I/O investigation evidence requires a completed capture result.");
        }

        var report = result.Report;
        var highestP95 = BuildHighestObservedP95(result.ResponseTimings);
        var largestDisk = report.Disks
            .Where(static disk => disk.TotalBytes > 0)
            .OrderByDescending(static disk => disk.TotalBytes)
            .ThenByDescending(static disk => disk.TotalOperations)
            .ThenBy(static disk => disk.PhysicalDiskNumber)
            .Select(static disk => new DiskIoObservedByteDiskCue(
                disk.PhysicalDiskNumber,
                disk.TotalBytes,
                disk.TotalOperations,
                disk.UnattributedBytes,
                disk.AttributionCoveragePercent))
            .FirstOrDefault();
        var largestOwner = report.Disks
            .SelectMany(static disk => disk.Owners.Select(owner => (Disk: disk, Owner: owner)))
            .Where(static candidate => candidate.Owner.TotalBytes > 0)
            .OrderByDescending(static candidate => candidate.Owner.TotalBytes)
            .ThenByDescending(static candidate => candidate.Owner.TotalOperations)
            .ThenBy(static candidate => candidate.Disk.PhysicalDiskNumber)
            .ThenBy(static candidate => candidate.Owner.Owner.ProcessId)
            .ThenBy(static candidate => candidate.Owner.Owner.StartedAt)
            .Select(static candidate => new DiskIoObservedOwnerBytesCue(
                candidate.Disk.PhysicalDiskNumber,
                candidate.Owner.Owner,
                candidate.Owner.TotalBytes,
                candidate.Owner.TotalOperations,
                candidate.Owner.ObservedByteSharePercent))
            .FirstOrDefault();

        return new DiskIoInvestigationSummary(
            report.AcceptedEventCount,
            result.EvidenceMayBeIncomplete,
            highestP95,
            largestDisk,
            largestOwner);
    }

    private static DiskIoObservedP95Cue? BuildHighestObservedP95(
        IReadOnlyList<DiskIoDiskResponseTiming> responseTimings)
    {
        var cues = new List<DiskIoObservedP95Cue>();
        foreach (var disk in responseTimings)
        {
            AddIfEligible(cues, disk.PhysicalDiskNumber, DiskIoOperationKind.Read, disk.Reads);
            AddIfEligible(cues, disk.PhysicalDiskNumber, DiskIoOperationKind.Write, disk.Writes);
            AddIfEligible(cues, disk.PhysicalDiskNumber, DiskIoOperationKind.Flush, disk.Flushes);
        }

        return cues
            .OrderByDescending(static cue => cue.P95)
            .ThenBy(static cue => cue.PhysicalDiskNumber)
            .ThenBy(static cue => cue.Operation)
            .FirstOrDefault();
    }

    private static void AddIfEligible(
        ICollection<DiskIoObservedP95Cue> cues,
        uint physicalDiskNumber,
        DiskIoOperationKind operation,
        DiskIoResponseTimingSummary? timing)
    {
        if (timing?.P95 is not null)
        {
            cues.Add(new DiskIoObservedP95Cue(
                physicalDiskNumber,
                operation,
                timing));
        }
    }
}
