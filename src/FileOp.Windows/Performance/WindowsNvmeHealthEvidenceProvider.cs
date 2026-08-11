using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FileOp.Core.Performance;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Performance;

internal sealed record WindowsNvmeHealthQueryResult(
    byte[]? Data,
    int BytesReturned,
    int Win32Error)
{
    public bool Succeeded => Data is not null && BytesReturned > 0;
}

internal interface IWindowsNvmeHealthStorageApi
{
    WindowsPhysicalDiskOpenResult Open(int physicalDiskNumber);

    WindowsNvmeHealthQueryResult QueryHealth(SafeFileHandle handle);
}

internal sealed class WindowsNvmeHealthStorageApi : IWindowsNvmeHealthStorageApi
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private readonly WindowsPhysicalDiskStorageApi _physicalDiskApi = new();

    public WindowsPhysicalDiskOpenResult Open(int physicalDiskNumber) =>
        _physicalDiskApi.Open(physicalDiskNumber);

    public WindowsNvmeHealthQueryResult QueryHealth(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException(
                "A valid physical-disk handle is required.",
                nameof(handle));
        }

        var input = WindowsNvmeHealthQueryCodec.BuildHealthQueryBuffer();
        var output = new byte[WindowsNvmeHealthQueryCodec.QueryBufferBytes];
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
            return new WindowsNvmeHealthQueryResult(
                null,
                0,
                Marshal.GetLastWin32Error());
        }

        if (bytesReturned > (uint)output.Length)
        {
            return new WindowsNvmeHealthQueryResult(null, 0, 13); // ERROR_INVALID_DATA.
        }

        return new WindowsNvmeHealthQueryResult(
            output,
            checked((int)bytesReturned),
            0);
    }

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

public sealed class WindowsNvmeHealthEvidenceProvider : INvmeHealthEvidenceProvider
{
    private readonly IWindowsNvmeHealthStorageApi _api;

    public WindowsNvmeHealthEvidenceProvider()
        : this(new WindowsNvmeHealthStorageApi())
    {
    }

    internal WindowsNvmeHealthEvidenceProvider(IWindowsNvmeHealthStorageApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public NvmeHealthEvidenceResult Query(int physicalDiskNumber)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        if (!OperatingSystem.IsWindows())
        {
            return NvmeHealthEvidenceResult.Unavailable(
                physicalDiskNumber,
                NvmeHealthEvidenceStatus.Unsupported,
                "Standardized NVMe SMART/Health evidence is available only on Windows.");
        }

        var open = _api.Open(physicalDiskNumber);
        if (!open.Opened)
        {
            return NvmeHealthEvidenceResult.Unavailable(
                physicalDiskNumber,
                NvmeHealthEvidenceStatus.Unavailable,
                $"PhysicalDrive{physicalDiskNumber} could not be opened for the read-only NVMe health query (Win32 {open.Win32Error}).");
        }

        using var handle = open.Handle!;
        var query = _api.QueryHealth(handle);
        if (!query.Succeeded)
        {
            var status = IsUnsupportedProtocolError(query.Win32Error)
                ? NvmeHealthEvidenceStatus.Unsupported
                : NvmeHealthEvidenceStatus.Unavailable;
            return NvmeHealthEvidenceResult.Unavailable(
                physicalDiskNumber,
                status,
                $"PhysicalDrive{physicalDiskNumber} did not return standardized NVMe SMART/Health evidence (Win32 {query.Win32Error}).");
        }

        try
        {
            var evidence = WindowsNvmeHealthQueryCodec.ParseHealthResponse(
                physicalDiskNumber,
                query.Data!,
                query.BytesReturned);
            return NvmeHealthEvidenceResult.Available(
                evidence,
                $"Standardized NVMe SMART/Health log evidence captured for PhysicalDrive{physicalDiskNumber}; values are reported fields, not a FileOp health score.");
        }
        catch (InvalidDataException exception)
        {
            return NvmeHealthEvidenceResult.Unavailable(
                physicalDiskNumber,
                NvmeHealthEvidenceStatus.Unavailable,
                $"PhysicalDrive{physicalDiskNumber} returned malformed NVMe SMART/Health evidence: {exception.Message}");
        }
    }

    private static bool IsUnsupportedProtocolError(int win32Error) =>
        win32Error is 1 or 50 or 87;
}

internal static class WindowsNvmeHealthQueryCodec
{
    internal const int StoragePropertyQueryHeaderBytes = 8;
    internal const int ProtocolSpecificDataBytes = 40;
    internal const int ProtocolDataDescriptorBytes = 48;
    internal const int NvmeHealthLogBytes = 512;
    internal const int QueryBufferBytes =
        StoragePropertyQueryHeaderBytes + ProtocolSpecificDataBytes + NvmeHealthLogBytes;

    internal const uint StorageDeviceProtocolSpecificProperty = 50;
    internal const uint PropertyStandardQuery = 0;
    internal const uint ProtocolTypeNvme = 3;
    internal const uint NvmeDataTypeLogPage = 2;
    internal const uint NvmeLogPageHealthInfo = 2;

    public static byte[] BuildHealthQueryBuffer()
    {
        var buffer = new byte[QueryBufferBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(0, 4),
            StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(4, 4),
            PropertyStandardQuery);

        var protocol = buffer.AsSpan(StoragePropertyQueryHeaderBytes, ProtocolSpecificDataBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[0..4], ProtocolTypeNvme);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[4..8], NvmeDataTypeLogPage);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[8..12], NvmeLogPageHealthInfo);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[12..16], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[16..20], ProtocolSpecificDataBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[20..24], NvmeHealthLogBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[24..28], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[28..32], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[32..36], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(protocol[36..40], 0);
        return buffer;
    }

    public static NvmeHealthEvidence ParseHealthResponse(
        int physicalDiskNumber,
        ReadOnlySpan<byte> data,
        int bytesReturned)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        if (bytesReturned < ProtocolDataDescriptorBytes ||
            bytesReturned > data.Length)
        {
            throw new InvalidDataException(
                $"Returned NVMe protocol data length {bytesReturned} is outside the {data.Length}-byte response buffer.");
        }

        var returned = data[..bytesReturned];
        var version = BinaryPrimitives.ReadUInt32LittleEndian(returned[0..4]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(returned[4..8]);
        if (version != ProtocolDataDescriptorBytes ||
            size != ProtocolDataDescriptorBytes)
        {
            throw new InvalidDataException(
                $"NVMe protocol descriptor version/size {version}/{size} does not match the expected {ProtocolDataDescriptorBytes}-byte descriptor.");
        }

        var protocol = returned.Slice(StoragePropertyQueryHeaderBytes, ProtocolSpecificDataBytes);
        var protocolType = BinaryPrimitives.ReadUInt32LittleEndian(protocol[0..4]);
        var dataType = BinaryPrimitives.ReadUInt32LittleEndian(protocol[4..8]);
        var requestValue = BinaryPrimitives.ReadUInt32LittleEndian(protocol[8..12]);
        var protocolDataOffset = BinaryPrimitives.ReadUInt32LittleEndian(protocol[16..20]);
        var protocolDataLength = BinaryPrimitives.ReadUInt32LittleEndian(protocol[20..24]);
        if (protocolType != ProtocolTypeNvme ||
            dataType != NvmeDataTypeLogPage ||
            requestValue != NvmeLogPageHealthInfo)
        {
            throw new InvalidDataException(
                $"Returned protocol metadata {protocolType}/{dataType}/{requestValue} does not describe the requested NVMe SMART/Health log.");
        }
        if (protocolDataOffset < ProtocolSpecificDataBytes ||
            protocolDataLength < NvmeHealthLogBytes)
        {
            throw new InvalidDataException(
                $"Returned NVMe health offset/length {protocolDataOffset}/{protocolDataLength} is too small.");
        }

        var healthStartLong = (long)StoragePropertyQueryHeaderBytes + protocolDataOffset;
        var healthEndLong = healthStartLong + NvmeHealthLogBytes;
        if (healthStartLong < ProtocolDataDescriptorBytes ||
            healthStartLong > int.MaxValue ||
            healthEndLong > bytesReturned)
        {
            throw new InvalidDataException(
                $"Returned NVMe health payload range {healthStartLong}..{healthEndLong} exceeds {bytesReturned} returned bytes.");
        }

        var healthStart = checked((int)healthStartLong);
        return ParseHealthLog(
            physicalDiskNumber,
            returned.Slice(healthStart, NvmeHealthLogBytes));
    }

    public static NvmeHealthEvidence ParseHealthLog(
        int physicalDiskNumber,
        ReadOnlySpan<byte> healthLog)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        if (healthLog.Length < NvmeHealthLogBytes)
        {
            throw new InvalidDataException(
                $"NVMe SMART/Health log is {healthLog.Length} byte(s); {NvmeHealthLogBytes} are required.");
        }

        var spare = healthLog[3];
        var spareThreshold = healthLog[4];
        if (spare > 100 || spareThreshold > 100)
        {
            throw new InvalidDataException(
                $"NVMe normalized spare values {spare}/{spareThreshold} are outside 0..100.");
        }

        return new NvmeHealthEvidence(
            physicalDiskNumber,
            new NvmeCriticalWarningEvidence(healthLog[0]),
            BinaryPrimitives.ReadUInt16LittleEndian(healthLog[1..3]),
            spare,
            spareThreshold,
            healthLog[5],
            ReadUInt128LittleEndian(healthLog[112..128]),
            ReadUInt128LittleEndian(healthLog[128..144]),
            ReadUInt128LittleEndian(healthLog[144..160]),
            ReadUInt128LittleEndian(healthLog[160..176]),
            ReadUInt128LittleEndian(healthLog[176..192]),
            BinaryPrimitives.ReadUInt32LittleEndian(healthLog[192..196]),
            BinaryPrimitives.ReadUInt32LittleEndian(healthLog[196..200]));
    }

    private static UInt128 ReadUInt128LittleEndian(ReadOnlySpan<byte> value)
    {
        if (value.Length != 16)
        {
            throw new ArgumentException(
                "An NVMe 128-bit counter requires exactly 16 bytes.",
                nameof(value));
        }

        var low = BinaryPrimitives.ReadUInt64LittleEndian(value[0..8]);
        var high = BinaryPrimitives.ReadUInt64LittleEndian(value[8..16]);
        return ((UInt128)high << 64) | low;
    }
}
