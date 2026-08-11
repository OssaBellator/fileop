namespace FileOp.Core.Performance;

public enum NvmeHealthEvidenceStatus
{
    Available,
    Unsupported,
    Unavailable,
}

public sealed record NvmeCriticalWarningEvidence
{
    private const byte KnownMask = 0x1F;

    public NvmeCriticalWarningEvidence(byte rawValue)
    {
        RawValue = rawValue;
    }

    public byte RawValue { get; }
    public bool AvailableSpareBelowThreshold => (RawValue & 0x01) != 0;
    public bool TemperatureThreshold => (RawValue & 0x02) != 0;
    public bool ReliabilityDegraded => (RawValue & 0x04) != 0;
    public bool MediaReadOnly => (RawValue & 0x08) != 0;
    public bool VolatileMemoryBackupFailed => (RawValue & 0x10) != 0;
    public byte ReservedOrFutureBits => (byte)(RawValue & ~KnownMask);
    public bool HasDefinedCriticalWarning => (RawValue & KnownMask) != 0;
}

public sealed record NvmeHealthEvidence
{
    public NvmeHealthEvidence(
        int physicalDiskNumber,
        NvmeCriticalWarningEvidence criticalWarnings,
        ushort compositeTemperatureKelvin,
        byte availableSparePercent,
        byte availableSpareThresholdPercent,
        byte percentageUsedEstimate,
        UInt128 powerCycles,
        UInt128 powerOnHours,
        UInt128 unsafeShutdowns,
        UInt128 mediaErrors,
        UInt128 errorInfoLogEntryCount,
        uint warningCompositeTemperatureMinutes,
        uint criticalCompositeTemperatureMinutes)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        ArgumentNullException.ThrowIfNull(criticalWarnings);
        if (availableSparePercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableSparePercent),
                availableSparePercent,
                "NVMe available spare is a normalized percentage from 0 to 100.");
        }
        if (availableSpareThresholdPercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availableSpareThresholdPercent),
                availableSpareThresholdPercent,
                "NVMe available spare threshold is a normalized percentage from 0 to 100.");
        }

        PhysicalDiskNumber = physicalDiskNumber;
        CriticalWarnings = criticalWarnings;
        CompositeTemperatureKelvin = compositeTemperatureKelvin;
        AvailableSparePercent = availableSparePercent;
        AvailableSpareThresholdPercent = availableSpareThresholdPercent;
        PercentageUsedEstimate = percentageUsedEstimate;
        PowerCycles = powerCycles;
        PowerOnHours = powerOnHours;
        UnsafeShutdowns = unsafeShutdowns;
        MediaErrors = mediaErrors;
        ErrorInfoLogEntryCount = errorInfoLogEntryCount;
        WarningCompositeTemperatureMinutes = warningCompositeTemperatureMinutes;
        CriticalCompositeTemperatureMinutes = criticalCompositeTemperatureMinutes;
    }

    public int PhysicalDiskNumber { get; }
    public NvmeCriticalWarningEvidence CriticalWarnings { get; }
    public ushort CompositeTemperatureKelvin { get; }
    public byte AvailableSparePercent { get; }
    public byte AvailableSpareThresholdPercent { get; }

    // NVMe defines this as a vendor-specific estimate of percentage of life used.
    // Values can exceed 100 and 255 represents values above 254.
    public byte PercentageUsedEstimate { get; }

    public UInt128 PowerCycles { get; }
    public UInt128 PowerOnHours { get; }
    public UInt128 UnsafeShutdowns { get; }
    public UInt128 MediaErrors { get; }
    public UInt128 ErrorInfoLogEntryCount { get; }
    public uint WarningCompositeTemperatureMinutes { get; }
    public uint CriticalCompositeTemperatureMinutes { get; }
}

public sealed record NvmeHealthEvidenceResult
{
    public NvmeHealthEvidenceResult(
        int physicalDiskNumber,
        NvmeHealthEvidenceStatus status,
        NvmeHealthEvidence? evidence,
        string detail)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if ((status == NvmeHealthEvidenceStatus.Available) != (evidence is not null))
        {
            throw new ArgumentException(
                "Available NVMe health results require evidence; unavailable results cannot carry it.",
                nameof(evidence));
        }
        if (evidence is not null && evidence.PhysicalDiskNumber != physicalDiskNumber)
        {
            throw new ArgumentException(
                "NVMe health result number must match its evidence.",
                nameof(evidence));
        }

        PhysicalDiskNumber = physicalDiskNumber;
        Status = status;
        Evidence = evidence;
        Detail = detail;
    }

    public int PhysicalDiskNumber { get; }
    public NvmeHealthEvidenceStatus Status { get; }
    public NvmeHealthEvidence? Evidence { get; }
    public string Detail { get; }

    public static NvmeHealthEvidenceResult Available(
        NvmeHealthEvidence evidence,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new NvmeHealthEvidenceResult(
            evidence.PhysicalDiskNumber,
            NvmeHealthEvidenceStatus.Available,
            evidence,
            detail);
    }

    public static NvmeHealthEvidenceResult Unavailable(
        int physicalDiskNumber,
        NvmeHealthEvidenceStatus status,
        string detail)
    {
        if (status == NvmeHealthEvidenceStatus.Available)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new NvmeHealthEvidenceResult(
            physicalDiskNumber,
            status,
            null,
            detail);
    }
}

public interface INvmeHealthEvidenceProvider
{
    NvmeHealthEvidenceResult Query(int physicalDiskNumber);
}
