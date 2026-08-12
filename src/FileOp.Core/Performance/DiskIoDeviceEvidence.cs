using System.Collections;
using System.Diagnostics;

namespace FileOp.Core.Performance;

public enum DiskIoDeviceEvidenceQueryStatus
{
    Queried,
    DiskNumberOutOfRange,
    QueryBudgetExceeded,
}

public sealed record DiskIoPhysicalDiskDeviceEvidence
{
    public DiskIoPhysicalDiskDeviceEvidence(
        uint physicalDiskNumber,
        DiskIoDeviceEvidenceQueryStatus queryStatus,
        PhysicalDiskDeviceContextResult? deviceContext,
        NvmeHealthEvidenceResult? nvmeHealth)
        : this(
            physicalDiskNumber,
            queryStatus,
            deviceContext,
            nvmeHealth,
            null)
    {
    }

    public DiskIoPhysicalDiskDeviceEvidence(
        uint physicalDiskNumber,
        DiskIoDeviceEvidenceQueryStatus queryStatus,
        PhysicalDiskDeviceContextResult? deviceContext,
        NvmeHealthEvidenceResult? nvmeHealth,
        PhysicalDiskFailurePredictionResult? failurePrediction)
    {
        if (!Enum.IsDefined(queryStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(queryStatus));
        }

        if (queryStatus == DiskIoDeviceEvidenceQueryStatus.Queried)
        {
            if (physicalDiskNumber > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(physicalDiskNumber),
                    physicalDiskNumber,
                    "A queried physical disk must fit the signed PhysicalDrive query range.");
            }

            var queryNumber = checked((int)physicalDiskNumber);
            ArgumentNullException.ThrowIfNull(deviceContext);
            ArgumentNullException.ThrowIfNull(nvmeHealth);
            if (deviceContext.PhysicalDiskNumber != queryNumber ||
                nvmeHealth.PhysicalDiskNumber != queryNumber ||
                (failurePrediction is not null &&
                    failurePrediction.PhysicalDiskNumber != queryNumber))
            {
                throw new ArgumentException(
                    "Disk I/O device evidence must match its physical-disk number.");
            }
        }
        else if (deviceContext is not null ||
            nvmeHealth is not null ||
            failurePrediction is not null)
        {
            throw new ArgumentException(
                "A skipped physical-disk device query cannot carry provider evidence.");
        }

        if (queryStatus == DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange &&
            physicalDiskNumber <= int.MaxValue)
        {
            throw new ArgumentException(
                "Disk-number-out-of-range evidence requires a number above the signed PhysicalDrive query range.",
                nameof(queryStatus));
        }
        if (queryStatus == DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded &&
            physicalDiskNumber > int.MaxValue)
        {
            throw new ArgumentException(
                "Out-of-range physical disk numbers must use the out-of-range query status.",
                nameof(queryStatus));
        }

        PhysicalDiskNumber = physicalDiskNumber;
        QueryStatus = queryStatus;
        DeviceContext = deviceContext;
        NvmeHealth = nvmeHealth;
        FailurePrediction = failurePrediction;
    }

    public uint PhysicalDiskNumber { get; }
    public DiskIoDeviceEvidenceQueryStatus QueryStatus { get; }
    public PhysicalDiskDeviceContextResult? DeviceContext { get; }
    public NvmeHealthEvidenceResult? NvmeHealth { get; }
    public PhysicalDiskFailurePredictionResult? FailurePrediction { get; }

    public bool QueryAttempted => QueryStatus == DiskIoDeviceEvidenceQueryStatus.Queried;

    public string QueryStatusDetail => QueryStatus switch
    {
        DiskIoDeviceEvidenceQueryStatus.Queried =>
            FailurePrediction is null
                ? $"Device context {DeviceContext!.Status}; standardized NVMe health {NvmeHealth!.Status}; Windows failure prediction not attached in this compatibility result."
                : $"Device context {DeviceContext!.Status}; standardized NVMe health {NvmeHealth!.Status}; Windows failure prediction {FailurePrediction.Status}.",
        DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange =>
            $"Physical disk number {PhysicalDiskNumber} exceeds FileOp's signed PhysicalDrive query range; no device metadata query was attempted.",
        DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded =>
            $"Physical disk {PhysicalDiskNumber} was observed after FileOp's bounded post-capture device-query budget was exhausted; no device metadata query was attempted.",
        _ => throw new InvalidOperationException("Unknown disk device-evidence query status."),
    };
}

public sealed record DiskIoDeviceEvidenceSnapshot
    : IReadOnlyList<DiskIoPhysicalDiskDeviceEvidence>
{
    private readonly DiskIoPhysicalDiskDeviceEvidence[] _rows;

    public DiskIoDeviceEvidenceSnapshot(
        IReadOnlyList<DiskIoPhysicalDiskDeviceEvidence> rows,
        TimeSpan queryElapsed)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (queryElapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(queryElapsed));
        }

        _rows = rows.ToArray();
        foreach (var row in _rows)
        {
            ArgumentNullException.ThrowIfNull(row);
        }
        QueryElapsed = queryElapsed;
    }

    public TimeSpan QueryElapsed { get; }
    public int Count => _rows.Length;
    public DiskIoPhysicalDiskDeviceEvidence this[int index] => _rows[index];
    public IReadOnlyList<DiskIoPhysicalDiskDeviceEvidence> Rows => _rows;

    public IEnumerator<DiskIoPhysicalDiskDeviceEvidence> GetEnumerator() =>
        ((IEnumerable<DiskIoPhysicalDiskDeviceEvidence>)_rows).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class DiskIoDeviceEvidenceCollector
{
    public const int MaximumQueriedPhysicalDisks = 32;

    public static DiskIoDeviceEvidenceSnapshot Query(
        IReadOnlyList<uint> physicalDiskNumbers,
        IPhysicalDiskDeviceContextProvider deviceContextProvider,
        INvmeHealthEvidenceProvider nvmeHealthProvider)
    {
        ArgumentNullException.ThrowIfNull(physicalDiskNumbers);
        ArgumentNullException.ThrowIfNull(deviceContextProvider);
        ArgumentNullException.ThrowIfNull(nvmeHealthProvider);

        var queryStart = Stopwatch.GetTimestamp();
        var numbers = physicalDiskNumbers.ToArray();
        var seen = new HashSet<uint>();
        foreach (var number in numbers)
        {
            if (!seen.Add(number))
            {
                throw new ArgumentException(
                    $"Physical disk {number} appears more than once in the device-evidence query set.",
                    nameof(physicalDiskNumbers));
            }
        }

        var rows = new DiskIoPhysicalDiskDeviceEvidence[numbers.Length];
        var queriedCount = 0;
        for (var index = 0; index < numbers.Length; index++)
        {
            var number = numbers[index];
            if (number > int.MaxValue)
            {
                rows[index] = new DiskIoPhysicalDiskDeviceEvidence(
                    number,
                    DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange,
                    null,
                    null);
                continue;
            }
            if (queriedCount >= MaximumQueriedPhysicalDisks)
            {
                rows[index] = new DiskIoPhysicalDiskDeviceEvidence(
                    number,
                    DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded,
                    null,
                    null);
                continue;
            }

            var queryNumber = checked((int)number);
            var deviceContext = deviceContextProvider.Query(queryNumber);
            var nvmeHealth = nvmeHealthProvider.Query(queryNumber);
            rows[index] = new DiskIoPhysicalDiskDeviceEvidence(
                number,
                DiskIoDeviceEvidenceQueryStatus.Queried,
                deviceContext,
                nvmeHealth);
            queriedCount++;
        }

        return new DiskIoDeviceEvidenceSnapshot(
            rows,
            Stopwatch.GetElapsedTime(queryStart));
    }

    public static DiskIoDeviceEvidenceSnapshot AttachFailurePrediction(
        DiskIoDeviceEvidenceSnapshot snapshot,
        IPhysicalDiskFailurePredictionProvider failurePredictionProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(failurePredictionProvider);

        var started = Stopwatch.GetTimestamp();
        var rows = new DiskIoPhysicalDiskDeviceEvidence[snapshot.Count];
        for (var index = 0; index < snapshot.Count; index++)
        {
            var row = snapshot[index];
            if (!row.QueryAttempted)
            {
                rows[index] = row;
                continue;
            }
            if (row.FailurePrediction is not null)
            {
                throw new ArgumentException(
                    $"Physical disk {row.PhysicalDiskNumber} already has failure-prediction evidence.",
                    nameof(snapshot));
            }

            var queryNumber = checked((int)row.PhysicalDiskNumber);
            var failurePrediction = failurePredictionProvider.Query(queryNumber);
            rows[index] = new DiskIoPhysicalDiskDeviceEvidence(
                row.PhysicalDiskNumber,
                row.QueryStatus,
                row.DeviceContext,
                row.NvmeHealth,
                failurePrediction);
        }

        return new DiskIoDeviceEvidenceSnapshot(
            rows,
            snapshot.QueryElapsed + Stopwatch.GetElapsedTime(started));
    }
}
