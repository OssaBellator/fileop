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
                nvmeHealth.PhysicalDiskNumber != queryNumber)
            {
                throw new ArgumentException(
                    "Disk I/O device evidence must match its physical-disk number.");
            }
        }
        else if (deviceContext is not null || nvmeHealth is not null)
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
    }

    public uint PhysicalDiskNumber { get; }
    public DiskIoDeviceEvidenceQueryStatus QueryStatus { get; }
    public PhysicalDiskDeviceContextResult? DeviceContext { get; }
    public NvmeHealthEvidenceResult? NvmeHealth { get; }

    public bool QueryAttempted => QueryStatus == DiskIoDeviceEvidenceQueryStatus.Queried;

    public string QueryStatusDetail => QueryStatus switch
    {
        DiskIoDeviceEvidenceQueryStatus.Queried =>
            $"Device context {DeviceContext!.Status}; standardized NVMe health {NvmeHealth!.Status}.",
        DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange =>
            $"Physical disk number {PhysicalDiskNumber} exceeds FileOp's signed PhysicalDrive query range; no device metadata query was attempted.",
        DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded =>
            $"Physical disk {PhysicalDiskNumber} was observed after FileOp's bounded post-capture device-query budget was exhausted; no device metadata query was attempted.",
        _ => throw new InvalidOperationException("Unknown disk device-evidence query status."),
    };
}

public static class DiskIoDeviceEvidenceCollector
{
    public const int MaximumQueriedPhysicalDisks = 32;

    public static IReadOnlyList<DiskIoPhysicalDiskDeviceEvidence> Query(
        IReadOnlyList<uint> physicalDiskNumbers,
        IPhysicalDiskDeviceContextProvider deviceContextProvider,
        INvmeHealthEvidenceProvider nvmeHealthProvider)
    {
        ArgumentNullException.ThrowIfNull(physicalDiskNumbers);
        ArgumentNullException.ThrowIfNull(deviceContextProvider);
        ArgumentNullException.ThrowIfNull(nvmeHealthProvider);

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

        return rows;
    }
}
