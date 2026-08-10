namespace FileOp.Core.Performance;

public enum DiskIoOperationKind
{
    Read,
    Write,
    Flush,
}

public sealed record DiskIoProcessIdentity(
    int ProcessId,
    DateTimeOffset? StartedAt,
    string? ImageName)
{
    public bool HasStableInstanceIdentity => StartedAt.HasValue;
}

public sealed record DiskIoEventObservation(
    DateTimeOffset Timestamp,
    uint PhysicalDiskNumber,
    DiskIoOperationKind Operation,
    long TransferBytes,
    DiskIoProcessIdentity? Owner);

public sealed record DiskIoProcessAttribution(
    DiskIoProcessIdentity Owner,
    long ReadBytes,
    long WriteBytes,
    long ReadOperations,
    long WriteOperations,
    long FlushOperations,
    double ObservedByteSharePercent)
{
    public long TotalBytes => SaturatingAdd(ReadBytes, WriteBytes);

    public long TotalOperations => SaturatingAdd(
        SaturatingAdd(ReadOperations, WriteOperations),
        FlushOperations);

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}

public sealed record DiskIoDiskAttribution(
    uint PhysicalDiskNumber,
    long ReadBytes,
    long WriteBytes,
    long ReadOperations,
    long WriteOperations,
    long FlushOperations,
    long UnattributedReadBytes,
    long UnattributedWriteBytes,
    long UnattributedReadOperations,
    long UnattributedWriteOperations,
    long UnattributedFlushOperations,
    long OtherIdentifiedReadBytes,
    long OtherIdentifiedWriteBytes,
    long OtherIdentifiedReadOperations,
    long OtherIdentifiedWriteOperations,
    long OtherIdentifiedFlushOperations,
    int OtherIdentifiedOwnerCount,
    IReadOnlyList<DiskIoProcessAttribution> Owners)
{
    public long TotalBytes => SaturatingAdd(ReadBytes, WriteBytes);

    public long TotalOperations => SaturatingAdd(
        SaturatingAdd(ReadOperations, WriteOperations),
        FlushOperations);

    public long UnattributedBytes => SaturatingAdd(
        UnattributedReadBytes,
        UnattributedWriteBytes);

    public long OtherIdentifiedBytes => SaturatingAdd(
        OtherIdentifiedReadBytes,
        OtherIdentifiedWriteBytes);

    public long IdentifiedBytes => Math.Max(0, TotalBytes - UnattributedBytes);

    public double? AttributionCoveragePercent => TotalBytes > 0
        ? Math.Clamp(IdentifiedBytes * 100d / TotalBytes, 0d, 100d)
        : null;

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}

public sealed record DiskIoAttributionReport(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int AcceptedEventCount,
    int MaxOwnersPerDisk,
    IReadOnlyList<DiskIoDiskAttribution> Disks)
{
    public TimeSpan ObservationDuration => EndedAt - StartedAt;
}

public static class DiskIoAttributionAnalyzer
{
    public const int DefaultMaxOwnersPerDisk = 12;
    public const int MaximumOwnersPerDisk = 100;

    public static DiskIoAttributionReport Analyze(
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<DiskIoEventObservation> observations,
        int maxOwnersPerDisk = DefaultMaxOwnersPerDisk)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (endedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endedAt),
                endedAt,
                "Disk-I/O attribution end time cannot precede the start time.");
        }

        if (maxOwnersPerDisk <= 0 || maxOwnersPerDisk > MaximumOwnersPerDisk)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOwnersPerDisk),
                maxOwnersPerDisk,
                $"Disk-I/O owner rows must be between 1 and {MaximumOwnersPerDisk} per physical disk.");
        }

        var disks = new Dictionary<uint, DiskAccumulator>();
        foreach (var observation in observations)
        {
            ValidateObservation(observation, startedAt, endedAt);
            if (!disks.TryGetValue(observation.PhysicalDiskNumber, out var disk))
            {
                disk = new DiskAccumulator(observation.PhysicalDiskNumber);
                disks.Add(observation.PhysicalDiskNumber, disk);
            }

            disk.Add(observation);
        }

        var result = disks.Values
            .OrderBy(static disk => disk.PhysicalDiskNumber)
            .Select(disk => disk.Build(maxOwnersPerDisk))
            .ToArray();
        return new DiskIoAttributionReport(
            startedAt,
            endedAt,
            observations.Count,
            maxOwnersPerDisk,
            result);
    }

    private static void ValidateObservation(
        DiskIoEventObservation observation,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        if (observation.Timestamp < startedAt || observation.Timestamp > endedAt)
        {
            throw new InvalidDataException(
                $"Disk-I/O observation at {observation.Timestamp:O} is outside the declared attribution window {startedAt:O}–{endedAt:O}.");
        }

        if (!Enum.IsDefined(observation.Operation))
        {
            throw new InvalidDataException(
                $"Disk-I/O observation has unsupported operation value {(int)observation.Operation}.");
        }

        if (observation.TransferBytes < 0)
        {
            throw new InvalidDataException(
                $"Disk-I/O observation has invalid negative transfer size {observation.TransferBytes}.");
        }

        if (observation.Operation == DiskIoOperationKind.Flush && observation.TransferBytes != 0)
        {
            throw new InvalidDataException(
                "Disk-I/O flush observations cannot invent transfer bytes; the disk completion event does not provide a transfer-size field for flushes.");
        }

        if (observation.Owner is { } owner)
        {
            if (owner.ProcessId <= 0)
            {
                throw new InvalidDataException(
                    $"Disk-I/O owner process ID {owner.ProcessId} is invalid.");
            }

            if (owner.ImageName is { Length: > 0 } imageName && string.IsNullOrWhiteSpace(imageName))
            {
                throw new InvalidDataException(
                    "Disk-I/O owner image name cannot contain only whitespace.");
            }
        }
    }

    private sealed class DiskAccumulator(uint physicalDiskNumber)
    {
        private readonly Dictionary<OwnerKey, OwnerAccumulator> _owners = [];
        private readonly TransferAccumulator _totals = new();
        private readonly TransferAccumulator _unattributed = new();

        public uint PhysicalDiskNumber { get; } = physicalDiskNumber;

        public void Add(DiskIoEventObservation observation)
        {
            _totals.Add(observation.Operation, observation.TransferBytes);
            if (observation.Owner is null)
            {
                _unattributed.Add(observation.Operation, observation.TransferBytes);
                return;
            }

            var owner = observation.Owner;
            var key = new OwnerKey(
                owner.ProcessId,
                owner.StartedAt?.ToUniversalTime().UtcDateTime.Ticks);
            if (!_owners.TryGetValue(key, out var accumulator))
            {
                accumulator = new OwnerAccumulator(owner);
                _owners.Add(key, accumulator);
            }

            accumulator.Add(observation.Operation, observation.TransferBytes);
        }

        public DiskIoDiskAttribution Build(int maxOwners)
        {
            var orderedOwners = _owners.Values
                .OrderByDescending(static owner => owner.TotalBytes)
                .ThenByDescending(static owner => owner.TotalOperations)
                .ThenBy(static owner => owner.Identity.ProcessId)
                .ThenBy(static owner => owner.Identity.StartedAt)
                .ToArray();
            var visible = orderedOwners.Take(maxOwners).ToArray();
            var hidden = orderedOwners.Skip(maxOwners).ToArray();
            var other = new TransferAccumulator();
            foreach (var owner in hidden)
            {
                other.Add(owner);
            }

            var totalBytes = _totals.TotalBytes;
            var rows = visible
                .Select(owner => owner.Build(
                    totalBytes > 0
                        ? Math.Clamp(owner.TotalBytes * 100d / totalBytes, 0d, 100d)
                        : 0d))
                .ToArray();

            return new DiskIoDiskAttribution(
                PhysicalDiskNumber,
                _totals.ReadBytes,
                _totals.WriteBytes,
                _totals.ReadOperations,
                _totals.WriteOperations,
                _totals.FlushOperations,
                _unattributed.ReadBytes,
                _unattributed.WriteBytes,
                _unattributed.ReadOperations,
                _unattributed.WriteOperations,
                _unattributed.FlushOperations,
                other.ReadBytes,
                other.WriteBytes,
                other.ReadOperations,
                other.WriteOperations,
                other.FlushOperations,
                hidden.Length,
                rows);
        }
    }

    private sealed class OwnerAccumulator(DiskIoProcessIdentity identity)
    {
        private readonly TransferAccumulator _transfer = new();

        public DiskIoProcessIdentity Identity { get; } = identity;

        public long TotalBytes => _transfer.TotalBytes;

        public long TotalOperations => _transfer.TotalOperations;

        public void Add(DiskIoOperationKind operation, long transferBytes) =>
            _transfer.Add(operation, transferBytes);

        public DiskIoProcessAttribution Build(double observedByteSharePercent) =>
            new(
                Identity,
                _transfer.ReadBytes,
                _transfer.WriteBytes,
                _transfer.ReadOperations,
                _transfer.WriteOperations,
                _transfer.FlushOperations,
                observedByteSharePercent);
    }

    private sealed class TransferAccumulator
    {
        public long ReadBytes { get; private set; }
        public long WriteBytes { get; private set; }
        public long ReadOperations { get; private set; }
        public long WriteOperations { get; private set; }
        public long FlushOperations { get; private set; }

        public long TotalBytes => SaturatingAdd(ReadBytes, WriteBytes);

        public long TotalOperations => SaturatingAdd(
            SaturatingAdd(ReadOperations, WriteOperations),
            FlushOperations);

        public void Add(DiskIoOperationKind operation, long transferBytes)
        {
            switch (operation)
            {
                case DiskIoOperationKind.Read:
                    ReadBytes = SaturatingAdd(ReadBytes, transferBytes);
                    ReadOperations = SaturatingAdd(ReadOperations, 1);
                    break;
                case DiskIoOperationKind.Write:
                    WriteBytes = SaturatingAdd(WriteBytes, transferBytes);
                    WriteOperations = SaturatingAdd(WriteOperations, 1);
                    break;
                case DiskIoOperationKind.Flush:
                    FlushOperations = SaturatingAdd(FlushOperations, 1);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported disk-I/O operation {operation}.");
            }
        }

        public void Add(OwnerAccumulator owner)
        {
            ReadBytes = SaturatingAdd(ReadBytes, owner._transfer.ReadBytes);
            WriteBytes = SaturatingAdd(WriteBytes, owner._transfer.WriteBytes);
            ReadOperations = SaturatingAdd(ReadOperations, owner._transfer.ReadOperations);
            WriteOperations = SaturatingAdd(WriteOperations, owner._transfer.WriteOperations);
            FlushOperations = SaturatingAdd(FlushOperations, owner._transfer.FlushOperations);
        }

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private readonly record struct OwnerKey(int ProcessId, long? StartedUtcTicks);
}