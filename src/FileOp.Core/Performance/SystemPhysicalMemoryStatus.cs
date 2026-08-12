namespace FileOp.Core.Performance;

public sealed record SystemPhysicalMemoryStatus
{
    public SystemPhysicalMemoryStatus(
        ulong totalPhysicalBytes,
        ulong availablePhysicalBytes,
        uint windowsMemoryLoadPercent)
    {
        if (totalPhysicalBytes == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalPhysicalBytes),
                "System physical-memory evidence requires a nonzero total physical byte count.");
        }
        if (availablePhysicalBytes > totalPhysicalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(availablePhysicalBytes),
                availablePhysicalBytes,
                "Available physical bytes cannot exceed total physical bytes.");
        }
        if (windowsMemoryLoadPercent > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowsMemoryLoadPercent),
                windowsMemoryLoadPercent,
                "Windows memory-load percentage must be between 0 and 100.");
        }

        TotalPhysicalBytes = totalPhysicalBytes;
        AvailablePhysicalBytes = availablePhysicalBytes;
        WindowsMemoryLoadPercent = windowsMemoryLoadPercent;
    }

    public ulong TotalPhysicalBytes { get; }
    public ulong AvailablePhysicalBytes { get; }
    public ulong UsedPhysicalBytes => TotalPhysicalBytes - AvailablePhysicalBytes;

    // GlobalMemoryStatusEx reports dwMemoryLoad as Windows' approximate
    // percentage of physical memory in use. FileOp preserves that value rather
    // than requiring it to equal a ratio derived from the byte counters.
    public uint WindowsMemoryLoadPercent { get; }
}

public enum SystemPhysicalMemoryStatusAvailability
{
    Available,
    Unsupported,
    Unavailable,
}

public sealed record SystemPhysicalMemoryStatusResult
{
    public SystemPhysicalMemoryStatusResult(
        SystemPhysicalMemoryStatusAvailability availability,
        SystemPhysicalMemoryStatus? status,
        TimeSpan elapsed,
        string detail)
    {
        if (!Enum.IsDefined(availability))
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }
        if ((availability == SystemPhysicalMemoryStatusAvailability.Available) !=
            (status is not null))
        {
            throw new ArgumentException(
                "Available system physical-memory results require evidence; unavailable results cannot carry it.",
                nameof(status));
        }
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        Availability = availability;
        Status = status;
        Elapsed = elapsed;
        Detail = detail;
    }

    public SystemPhysicalMemoryStatusAvailability Availability { get; }
    public SystemPhysicalMemoryStatus? Status { get; }
    public TimeSpan Elapsed { get; }
    public string Detail { get; }

    public static SystemPhysicalMemoryStatusResult Available(
        SystemPhysicalMemoryStatus status,
        TimeSpan elapsed,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new SystemPhysicalMemoryStatusResult(
            SystemPhysicalMemoryStatusAvailability.Available,
            status,
            elapsed,
            detail);
    }

    public static SystemPhysicalMemoryStatusResult Unavailable(
        SystemPhysicalMemoryStatusAvailability availability,
        TimeSpan elapsed,
        string detail)
    {
        if (availability == SystemPhysicalMemoryStatusAvailability.Available)
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }
        return new SystemPhysicalMemoryStatusResult(
            availability,
            null,
            elapsed,
            detail);
    }
}

public interface ISystemPhysicalMemoryStatusProvider
{
    SystemPhysicalMemoryStatusResult Query();
}
