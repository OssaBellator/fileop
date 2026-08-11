using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FileOp.Core.Performance;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Performance;

internal sealed record WindowsPhysicalDiskOpenResult(
    SafeFileHandle? Handle,
    int Win32Error)
{
    public bool Opened => Handle is { IsInvalid: false, IsClosed: false };
}

internal sealed record WindowsStoragePropertyQueryResult(
    byte[]? Data,
    int Win32Error)
{
    public bool Succeeded => Data is not null;
}

internal interface IWindowsPhysicalDiskStorageApi
{
    WindowsPhysicalDiskOpenResult Open(int physicalDiskNumber);

    WindowsStoragePropertyQueryResult Query(
        SafeFileHandle handle,
        uint propertyId);
}

internal sealed class WindowsPhysicalDiskStorageApi : IWindowsPhysicalDiskStorageApi
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int PropertyQueryBytes = 12;
    private const int MaximumPropertyBytes = 64 * 1024;

    public WindowsPhysicalDiskOpenResult Open(int physicalDiskNumber)
    {
        var handle = CreateFileW(
            $"\\\\.\\PhysicalDrive{physicalDiskNumber}",
            desiredAccess: 0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            flagsAndAttributes: 0,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return new WindowsPhysicalDiskOpenResult(handle, 0);
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        return new WindowsPhysicalDiskOpenResult(null, error);
    }

    public WindowsStoragePropertyQueryResult Query(
        SafeFileHandle handle,
        uint propertyId)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException(
                "A valid physical-disk handle is required.",
                nameof(handle));
        }

        var input = new byte[PropertyQueryBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(0, 4), propertyId);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4, 4), 0); // PropertyStandardQuery.
        var output = new byte[MaximumPropertyBytes];
        if (!DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                input,
                checked((uint)input.Length),
                output,
                checked((uint)output.Length),
                out var bytesReturned,
                IntPtr.Zero))
        {
            return new WindowsStoragePropertyQueryResult(
                null,
                Marshal.GetLastWin32Error());
        }

        if (bytesReturned > output.Length)
        {
            return new WindowsStoragePropertyQueryResult(null, 13); // ERROR_INVALID_DATA.
        }

        Array.Resize(ref output, checked((int)bytesReturned));
        return new WindowsStoragePropertyQueryResult(output, 0);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}

public sealed class WindowsPhysicalDiskDeviceContextProvider
    : IPhysicalDiskDeviceContextProvider
{
    internal const uint StorageDeviceProperty = 0;
    internal const uint StorageDeviceSeekPenaltyProperty = 7;
    internal const uint StorageDeviceTrimProperty = 8;

    private readonly IWindowsPhysicalDiskStorageApi _api;

    public WindowsPhysicalDiskDeviceContextProvider()
        : this(new WindowsPhysicalDiskStorageApi())
    {
    }

    internal WindowsPhysicalDiskDeviceContextProvider(
        IWindowsPhysicalDiskStorageApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public PhysicalDiskDeviceContextResult Query(int physicalDiskNumber)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        if (!OperatingSystem.IsWindows())
        {
            return PhysicalDiskDeviceContextResult.Unavailable(
                physicalDiskNumber,
                PhysicalDiskDeviceContextStatus.Unsupported,
                "Physical-disk storage properties are available only on Windows.");
        }

        var open = _api.Open(physicalDiskNumber);
        if (!open.Opened)
        {
            return PhysicalDiskDeviceContextResult.Unavailable(
                physicalDiskNumber,
                PhysicalDiskDeviceContextStatus.Unavailable,
                $"PhysicalDrive{physicalDiskNumber} could not be opened for read-only metadata queries (Win32 {open.Win32Error}).");
        }

        using var handle = open.Handle!;
        var descriptorQuery = _api.Query(handle, StorageDeviceProperty);
        if (!descriptorQuery.Succeeded)
        {
            var status = IsUnsupportedPropertyError(descriptorQuery.Win32Error)
                ? PhysicalDiskDeviceContextStatus.Unsupported
                : PhysicalDiskDeviceContextStatus.Unavailable;
            return PhysicalDiskDeviceContextResult.Unavailable(
                physicalDiskNumber,
                status,
                $"PhysicalDrive{physicalDiskNumber} device descriptor query failed (Win32 {descriptorQuery.Win32Error}).");
        }

        PhysicalDiskDeviceDescriptor descriptor;
        try
        {
            descriptor = WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(
                physicalDiskNumber,
                descriptorQuery.Data!);
        }
        catch (InvalidDataException exception)
        {
            return PhysicalDiskDeviceContextResult.Unavailable(
                physicalDiskNumber,
                PhysicalDiskDeviceContextStatus.Unavailable,
                $"PhysicalDrive{physicalDiskNumber} returned malformed device descriptor data: {exception.Message}");
        }

        var seekPenalty = QueryBooleanCapability(
            handle,
            StorageDeviceSeekPenaltyProperty,
            "seek-penalty",
            "Incurs seek penalty",
            "Does not report a seek penalty");
        var trim = QueryBooleanCapability(
            handle,
            StorageDeviceTrimProperty,
            "TRIM",
            "TRIM is reported enabled",
            "TRIM is reported disabled");
        var context = new PhysicalDiskDeviceContext(descriptor, seekPenalty, trim);
        return PhysicalDiskDeviceContextResult.Available(
            context,
            $"Read-only storage property evidence captured for PhysicalDrive{physicalDiskNumber}; bus type is reported as {descriptor.BusTypeLabel} (raw {descriptor.RawBusType}).");
    }

    private PhysicalDiskBooleanCapability QueryBooleanCapability(
        SafeFileHandle handle,
        uint propertyId,
        string propertyName,
        string trueDetail,
        string falseDetail)
    {
        var query = _api.Query(handle, propertyId);
        if (!query.Succeeded)
        {
            return IsUnsupportedPropertyError(query.Win32Error)
                ? PhysicalDiskBooleanCapability.Unsupported(
                    $"The device/driver does not expose the {propertyName} storage property (Win32 {query.Win32Error}).")
                : PhysicalDiskBooleanCapability.Unavailable(
                    $"The {propertyName} storage property could not be read (Win32 {query.Win32Error}).");
        }

        try
        {
            var value = WindowsPhysicalDiskPropertyParser.ParseBooleanDescriptor(
                query.Data!,
                propertyName);
            return PhysicalDiskBooleanCapability.Available(
                value,
                value ? trueDetail : falseDetail);
        }
        catch (InvalidDataException exception)
        {
            return PhysicalDiskBooleanCapability.Unavailable(
                $"The {propertyName} storage property returned malformed data: {exception.Message}");
        }
    }

    private static bool IsUnsupportedPropertyError(int win32Error) =>
        win32Error is 1 or 50 or 87;
}

internal static class WindowsPhysicalDiskPropertyParser
{
    private const int DeviceDescriptorFixedBytes = 36;
    private const int BooleanDescriptorMinimumBytes = 9;
    private const uint MaximumReservedBusType = 0x7F;

    public static PhysicalDiskDeviceDescriptor ParseDeviceDescriptor(
        int physicalDiskNumber,
        ReadOnlySpan<byte> data)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        if (data.Length < DeviceDescriptorFixedBytes)
        {
            throw new InvalidDataException(
                $"Device descriptor is {data.Length} byte(s); at least {DeviceDescriptorFixedBytes} are required.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        if (version < DeviceDescriptorFixedBytes)
        {
            throw new InvalidDataException(
                $"Device descriptor version {version} is smaller than the fixed structure.");
        }
        if (size < DeviceDescriptorFixedBytes || size > data.Length)
        {
            throw new InvalidDataException(
                $"Device descriptor size {size} is outside the returned {data.Length}-byte buffer.");
        }

        var descriptorLength = checked((int)size);
        var descriptor = data[..descriptorLength];
        var rawBusType = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[28..32]);
        if (rawBusType > MaximumReservedBusType)
        {
            throw new InvalidDataException(
                $"Bus type value {rawBusType} exceeds STORAGE_BUS_TYPE's reserved maximum 0x{MaximumReservedBusType:X2}.");
        }

        return new PhysicalDiskDeviceDescriptor(
            physicalDiskNumber,
            rawBusType,
            FormatBusType(rawBusType),
            ReadOptionalAscii(descriptor, 12, "vendor ID"),
            ReadOptionalAscii(descriptor, 16, "product ID"),
            ReadOptionalAscii(descriptor, 20, "product revision"),
            ReadOptionalAscii(descriptor, 24, "serial number"),
            removableMedia: descriptor[10] != 0,
            commandQueueing: descriptor[11] != 0);
    }

    public static bool ParseBooleanDescriptor(
        ReadOnlySpan<byte> data,
        string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (data.Length < BooleanDescriptorMinimumBytes)
        {
            throw new InvalidDataException(
                $"{propertyName} descriptor is {data.Length} byte(s); at least {BooleanDescriptorMinimumBytes} are required.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        if (version < BooleanDescriptorMinimumBytes)
        {
            throw new InvalidDataException(
                $"{propertyName} descriptor version {version} is smaller than the known structure.");
        }
        if (size < BooleanDescriptorMinimumBytes || size > data.Length)
        {
            throw new InvalidDataException(
                $"{propertyName} descriptor size {size} is outside the returned {data.Length}-byte buffer.");
        }

        return data[8] != 0;
    }

    internal static string FormatBusType(uint rawBusType) =>
        rawBusType switch
        {
            0 => "Unknown",
            1 => "SCSI",
            2 => "ATAPI",
            3 => "ATA",
            4 => "IEEE 1394",
            5 => "SSA",
            6 => "Fibre Channel",
            7 => "USB",
            8 => "RAID",
            9 => "iSCSI",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            13 => "MMC",
            14 => "Virtual",
            15 => "File-backed virtual",
            16 => "Storage Spaces",
            17 => "NVMe",
            18 => "SCM",
            19 => "UFS",
            20 => "NVMe-oF",
            21 => "BusTypeMax sentinel",
            0x7F => "BusTypeMaxReserved sentinel",
            _ => $"Unrecognized bus type {rawBusType}",
        };

    private static string? ReadOptionalAscii(
        ReadOnlySpan<byte> descriptor,
        int offsetField,
        string fieldName)
    {
        var rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            descriptor.Slice(offsetField, 4));
        if (rawOffset == 0)
        {
            return null;
        }
        if (rawOffset < DeviceDescriptorFixedBytes || rawOffset >= descriptor.Length)
        {
            throw new InvalidDataException(
                $"{fieldName} offset {rawOffset} is outside the descriptor payload.");
        }

        var offset = checked((int)rawOffset);
        var remaining = descriptor[offset..];
        var terminator = remaining.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException(
                $"{fieldName} is not null-terminated inside the descriptor.");
        }

        var bytes = remaining[..terminator];
        foreach (var value in bytes)
        {
            if (value is < 0x20 or > 0x7E)
            {
                throw new InvalidDataException(
                    $"{fieldName} contains non-printable ASCII byte 0x{value:X2}.");
            }
        }

        var text = Encoding.ASCII.GetString(bytes).Trim();
        return text.Length == 0 ? null : text;
    }
}
