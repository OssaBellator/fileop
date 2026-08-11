namespace FileOp.Core.Performance;

public sealed record DiskIoPhysicalDiskDeviceEvidence
{
    public DiskIoPhysicalDiskDeviceEvidence(
        uint physicalDiskNumber,
        PhysicalDiskDeviceContextResult? deviceContext,
        NvmeHealthEvidenceResult? nvmeHealth)
    {
        if (physicalDiskNumber <= int.MaxValue)
        {
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
                "Physical-disk numbers outside the queryable signed range cannot carry device-query results.");
        }

        PhysicalDiskNumber = physicalDiskNumber;
        DeviceContext = deviceContext;
        NvmeHealth = nvmeHealth;
    }

    public uint PhysicalDiskNumber { get; }
    public PhysicalDiskDeviceContextResult? DeviceContext { get; }
    public NvmeHealthEvidenceResult? NvmeHealth { get; }

    public bool Queryable => PhysicalDiskNumber <= int.MaxValue;

    public string QueryStatusDetail => Queryable
        ? $"Device context {DeviceContext!.Status}; standardized NVMe health {NvmeHealth!.Status}."
        : $"Physical disk number {PhysicalDiskNumber} exceeds FileOp's signed PhysicalDrive query range; no device metadata query was attempted.";
}

public static class DiskIoDeviceEvidenceCollector
{
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
        for (var index = 0; index < numbers.Length; index++)
        {
            var number = numbers[index];
            if (number > int.MaxValue)
            {
                rows[index] = new DiskIoPhysicalDiskDeviceEvidence(number, null, null);
                continue;
            }

            var queryNumber = checked((int)number);
            var deviceContext = deviceContextProvider.Query(queryNumber);
            var nvmeHealth = nvmeHealthProvider.Query(queryNumber);
            rows[index] = new DiskIoPhysicalDiskDeviceEvidence(
                number,
                deviceContext,
                nvmeHealth);
        }

        return rows;
    }
}
