using System.Buffers.Binary;
using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

public sealed record WindowsDiskIoCompletion(
    uint PhysicalDiskNumber,
    DiskIoOperationKind Operation,
    long TransferBytes,
    ulong HighResolutionResponseTicks,
    uint IssuingThreadId,
    uint IrpFlags,
    long? ByteOffset);

public static class WindowsDiskIoEventDecoder
{
    public static readonly Guid DiskIoProviderId =
        new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");

    public const byte ReadEventType = 10;
    public const byte WriteEventType = 11;
    public const byte FlushEventType = 14;

    public static bool TryDecodeCompletion(
        Guid providerId,
        byte eventType,
        int pointerSize,
        ReadOnlySpan<byte> userData,
        out WindowsDiskIoCompletion? completion)
    {
        completion = null;
        if (providerId != DiskIoProviderId)
        {
            return false;
        }

        if (eventType is not (ReadEventType or WriteEventType or FlushEventType))
        {
            return false;
        }

        ValidatePointerSize(pointerSize);
        completion = eventType switch
        {
            ReadEventType => DecodeReadWrite(
                DiskIoOperationKind.Read,
                pointerSize,
                userData),
            WriteEventType => DecodeReadWrite(
                DiskIoOperationKind.Write,
                pointerSize,
                userData),
            FlushEventType => DecodeFlush(pointerSize, userData),
            _ => throw new InvalidOperationException(),
        };
        return true;
    }

    private static WindowsDiskIoCompletion DecodeReadWrite(
        DiskIoOperationKind operation,
        int pointerSize,
        ReadOnlySpan<byte> userData)
    {
        // DiskIo_TypeGroup1 (current Windows layout):
        // DiskNumber:u32, IrpFlags:u32, TransferSize:u32, Reserved:u32,
        // ByteOffset:i64, FileObject:pointer, Irp:pointer,
        // HighResResponseTime:u64, IssuingThreadId:u32.
        var highResolutionOffset = checked(24 + (2 * pointerSize));
        var issuingThreadOffset = checked(highResolutionOffset + sizeof(ulong));
        var requiredLength = checked(issuingThreadOffset + sizeof(uint));
        EnsureLength(userData, requiredLength, operation);

        return new WindowsDiskIoCompletion(
            BinaryPrimitives.ReadUInt32LittleEndian(userData.Slice(0, sizeof(uint))),
            operation,
            BinaryPrimitives.ReadUInt32LittleEndian(userData.Slice(8, sizeof(uint))),
            BinaryPrimitives.ReadUInt64LittleEndian(
                userData.Slice(highResolutionOffset, sizeof(ulong))),
            BinaryPrimitives.ReadUInt32LittleEndian(
                userData.Slice(issuingThreadOffset, sizeof(uint))),
            BinaryPrimitives.ReadUInt32LittleEndian(userData.Slice(4, sizeof(uint))),
            BinaryPrimitives.ReadInt64LittleEndian(userData.Slice(16, sizeof(long))));
    }

    private static WindowsDiskIoCompletion DecodeFlush(
        int pointerSize,
        ReadOnlySpan<byte> userData)
    {
        // DiskIo_TypeGroup3 (current Windows layout):
        // DiskNumber:u32, IrpFlags:u32, HighResResponseTime:u64,
        // Irp:pointer, IssuingThreadId:u32.
        var issuingThreadOffset = checked(16 + pointerSize);
        var requiredLength = checked(issuingThreadOffset + sizeof(uint));
        EnsureLength(userData, requiredLength, DiskIoOperationKind.Flush);

        return new WindowsDiskIoCompletion(
            BinaryPrimitives.ReadUInt32LittleEndian(userData.Slice(0, sizeof(uint))),
            DiskIoOperationKind.Flush,
            TransferBytes: 0,
            BinaryPrimitives.ReadUInt64LittleEndian(userData.Slice(8, sizeof(ulong))),
            BinaryPrimitives.ReadUInt32LittleEndian(
                userData.Slice(issuingThreadOffset, sizeof(uint))),
            BinaryPrimitives.ReadUInt32LittleEndian(userData.Slice(4, sizeof(uint))),
            ByteOffset: null);
    }

    private static void ValidatePointerSize(int pointerSize)
    {
        if (pointerSize is not (4 or 8))
        {
            throw new InvalidDataException(
                $"DiskIo ETW pointer size {pointerSize} is invalid; expected 4 or 8 bytes from the event-header pointer-size flags.");
        }
    }

    private static void EnsureLength(
        ReadOnlySpan<byte> userData,
        int requiredLength,
        DiskIoOperationKind operation)
    {
        if (userData.Length < requiredLength)
        {
            throw new InvalidDataException(
                $"DiskIo {operation} ETW payload is truncated: {userData.Length} byte(s), expected at least {requiredLength}.");
        }
    }
}
