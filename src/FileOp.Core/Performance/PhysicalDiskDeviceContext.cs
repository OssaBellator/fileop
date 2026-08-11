namespace FileOp.Core.Performance;

public enum PhysicalDiskDeviceContextStatus
{
    Available,
    Unsupported,
    Unavailable,
}

public enum PhysicalDiskCapabilityStatus
{
    Available,
    Unsupported,
    Unavailable,
}

public sealed record PhysicalDiskBooleanCapability
{
    public PhysicalDiskBooleanCapability(
        PhysicalDiskCapabilityStatus status,
        bool? value,
        string detail)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if ((status == PhysicalDiskCapabilityStatus.Available) != value.HasValue)
        {
            throw new ArgumentException(
                "Available physical-disk capability evidence requires a value; unavailable evidence cannot carry one.",
                nameof(value));
        }

        Status = status;
        Value = value;
        Detail = detail;
    }

    public PhysicalDiskCapabilityStatus Status { get; }
    public bool? Value { get; }
    public string Detail { get; }

    public static PhysicalDiskBooleanCapability Available(bool value, string detail) =>
        new(PhysicalDiskCapabilityStatus.Available, value, detail);

    public static PhysicalDiskBooleanCapability Unsupported(string detail) =>
        new(PhysicalDiskCapabilityStatus.Unsupported, null, detail);

    public static PhysicalDiskBooleanCapability Unavailable(string detail) =>
        new(PhysicalDiskCapabilityStatus.Unavailable, null, detail);
}

public sealed record PhysicalDiskDeviceDescriptor
{
    public PhysicalDiskDeviceDescriptor(
        int physicalDiskNumber,
        uint rawBusType,
        string busTypeLabel,
        string? vendorId,
        string? productId,
        string? productRevision,
        string? serialNumber,
        bool removableMedia,
        bool commandQueueing)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(busTypeLabel);

        PhysicalDiskNumber = physicalDiskNumber;
        RawBusType = rawBusType;
        BusTypeLabel = busTypeLabel;
        VendorId = NormalizeOptionalText(vendorId);
        ProductId = NormalizeOptionalText(productId);
        ProductRevision = NormalizeOptionalText(productRevision);
        SerialNumber = NormalizeOptionalText(serialNumber);
        RemovableMedia = removableMedia;
        CommandQueueing = commandQueueing;
    }

    public int PhysicalDiskNumber { get; }
    public uint RawBusType { get; }
    public string BusTypeLabel { get; }
    public string? VendorId { get; }
    public string? ProductId { get; }
    public string? ProductRevision { get; }
    public string? SerialNumber { get; }
    public bool RemovableMedia { get; }
    public bool CommandQueueing { get; }

    public string DisplayName =>
        VendorId is not null && ProductId is not null
            ? $"{VendorId} {ProductId}"
            : VendorId ?? ProductId ?? $"PhysicalDrive{PhysicalDiskNumber}";

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}

public sealed record PhysicalDiskDeviceContext
{
    public PhysicalDiskDeviceContext(
        PhysicalDiskDeviceDescriptor descriptor,
        PhysicalDiskBooleanCapability seekPenalty,
        PhysicalDiskBooleanCapability trim)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        SeekPenalty = seekPenalty ?? throw new ArgumentNullException(nameof(seekPenalty));
        Trim = trim ?? throw new ArgumentNullException(nameof(trim));
    }

    public PhysicalDiskDeviceDescriptor Descriptor { get; }
    public PhysicalDiskBooleanCapability SeekPenalty { get; }
    public PhysicalDiskBooleanCapability Trim { get; }
}

public sealed record PhysicalDiskDeviceContextResult
{
    public PhysicalDiskDeviceContextResult(
        int physicalDiskNumber,
        PhysicalDiskDeviceContextStatus status,
        PhysicalDiskDeviceContext? context,
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
        if ((status == PhysicalDiskDeviceContextStatus.Available) != (context is not null))
        {
            throw new ArgumentException(
                "Available physical-disk device context requires evidence; unavailable results cannot carry it.",
                nameof(context));
        }
        if (context is not null &&
            context.Descriptor.PhysicalDiskNumber != physicalDiskNumber)
        {
            throw new ArgumentException(
                "Physical-disk result number must match its descriptor.",
                nameof(context));
        }

        PhysicalDiskNumber = physicalDiskNumber;
        Status = status;
        Context = context;
        Detail = detail;
    }

    public int PhysicalDiskNumber { get; }
    public PhysicalDiskDeviceContextStatus Status { get; }
    public PhysicalDiskDeviceContext? Context { get; }
    public string Detail { get; }

    public static PhysicalDiskDeviceContextResult Available(
        PhysicalDiskDeviceContext context,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new PhysicalDiskDeviceContextResult(
            context.Descriptor.PhysicalDiskNumber,
            PhysicalDiskDeviceContextStatus.Available,
            context,
            detail);
    }

    public static PhysicalDiskDeviceContextResult Unavailable(
        int physicalDiskNumber,
        PhysicalDiskDeviceContextStatus status,
        string detail)
    {
        if (status == PhysicalDiskDeviceContextStatus.Available)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new PhysicalDiskDeviceContextResult(
            physicalDiskNumber,
            status,
            null,
            detail);
    }
}

public interface IPhysicalDiskDeviceContextProvider
{
    PhysicalDiskDeviceContextResult Query(int physicalDiskNumber);
}
