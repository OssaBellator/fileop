using System.Buffers.Binary;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoEventDecoderTests
{
    [DataTestMethod]
    [DataRow(4)]
    [DataRow(8)]
    public void ReadCompletionDecodesDocumentedFieldsForPointerWidth(int pointerSize)
    {
        var payload = CreateReadWritePayload(
            pointerSize,
            diskNumber: 7,
            irpFlags: 0xA1B2C3D4,
            transferSize: 65_536,
            byteOffset: 123_456_789,
            highResolutionResponseTicks: 987_654_321,
            issuingThreadId: 4_242);

        var decoded = WindowsDiskIoEventDecoder.TryDecodeCompletion(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            WindowsDiskIoEventDecoder.ReadEventType,
            pointerSize,
            payload,
            out var completion);

        Assert.IsTrue(decoded);
        Assert.IsNotNull(completion);
        Assert.AreEqual(7u, completion.PhysicalDiskNumber);
        Assert.AreEqual(DiskIoOperationKind.Read, completion.Operation);
        Assert.AreEqual(65_536L, completion.TransferBytes);
        Assert.AreEqual(987_654_321UL, completion.HighResolutionResponseTicks);
        Assert.AreEqual(4_242u, completion.IssuingThreadId);
        Assert.AreEqual(0xA1B2C3D4u, completion.IrpFlags);
        Assert.AreEqual(123_456_789L, completion.ByteOffset);
    }

    [DataTestMethod]
    [DataRow(4)]
    [DataRow(8)]
    public void WriteCompletionUsesSameTypeGroup1Layout(int pointerSize)
    {
        var payload = CreateReadWritePayload(
            pointerSize,
            diskNumber: 3,
            irpFlags: 0x10,
            transferSize: 4_096,
            byteOffset: -1,
            highResolutionResponseTicks: ulong.MaxValue - 5,
            issuingThreadId: uint.MaxValue - 1);

        var decoded = WindowsDiskIoEventDecoder.TryDecodeCompletion(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            WindowsDiskIoEventDecoder.WriteEventType,
            pointerSize,
            payload,
            out var completion);

        Assert.IsTrue(decoded);
        Assert.IsNotNull(completion);
        Assert.AreEqual(DiskIoOperationKind.Write, completion.Operation);
        Assert.AreEqual(4_096L, completion.TransferBytes);
        Assert.AreEqual(-1L, completion.ByteOffset);
        Assert.AreEqual(ulong.MaxValue - 5, completion.HighResolutionResponseTicks);
        Assert.AreEqual(uint.MaxValue - 1, completion.IssuingThreadId);
    }

    [DataTestMethod]
    [DataRow(4)]
    [DataRow(8)]
    public void FlushCompletionDecodesWithoutInventingTransferBytes(int pointerSize)
    {
        var payload = CreateFlushPayload(
            pointerSize,
            diskNumber: 11,
            irpFlags: 0x400,
            highResolutionResponseTicks: 123_000,
            issuingThreadId: 909);

        var decoded = WindowsDiskIoEventDecoder.TryDecodeCompletion(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            WindowsDiskIoEventDecoder.FlushEventType,
            pointerSize,
            payload,
            out var completion);

        Assert.IsTrue(decoded);
        Assert.IsNotNull(completion);
        Assert.AreEqual(11u, completion.PhysicalDiskNumber);
        Assert.AreEqual(DiskIoOperationKind.Flush, completion.Operation);
        Assert.AreEqual(0L, completion.TransferBytes);
        Assert.AreEqual(123_000UL, completion.HighResolutionResponseTicks);
        Assert.AreEqual(909u, completion.IssuingThreadId);
        Assert.AreEqual(0x400u, completion.IrpFlags);
        Assert.IsNull(completion.ByteOffset);
    }

    [TestMethod]
    public void NonDiskProviderAndNonCompletionTypesAreIgnored()
    {
        var payload = CreateReadWritePayload(8, 1, 2, 3, 4, 5, 6);

        Assert.IsFalse(WindowsDiskIoEventDecoder.TryDecodeCompletion(
            Guid.NewGuid(),
            WindowsDiskIoEventDecoder.ReadEventType,
            8,
            payload,
            out var wrongProvider));
        Assert.IsNull(wrongProvider);

        Assert.IsFalse(WindowsDiskIoEventDecoder.TryDecodeCompletion(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventType: 12,
            8,
            payload,
            out var initEvent));
        Assert.IsNull(initEvent);
    }

    [TestMethod]
    public void RecognizedCompletionRequiresUnambiguousPointerWidth()
    {
        var payload = CreateReadWritePayload(8, 1, 2, 3, 4, 5, 6);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoEventDecoder.TryDecodeCompletion(
                WindowsDiskIoEventDecoder.DiskIoProviderId,
                WindowsDiskIoEventDecoder.ReadEventType,
                pointerSize: 0,
                payload,
                out _));
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoEventDecoder.TryDecodeCompletion(
                WindowsDiskIoEventDecoder.DiskIoProviderId,
                WindowsDiskIoEventDecoder.ReadEventType,
                pointerSize: 16,
                payload,
                out _));
    }

    [DataTestMethod]
    [DataRow(4, 43)]
    [DataRow(8, 51)]
    public void TruncatedReadWritePayloadFailsClosed(int pointerSize, int truncatedLength)
    {
        var payload = new byte[truncatedLength];

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoEventDecoder.TryDecodeCompletion(
                WindowsDiskIoEventDecoder.DiskIoProviderId,
                WindowsDiskIoEventDecoder.ReadEventType,
                pointerSize,
                payload,
                out _));
    }

    [DataTestMethod]
    [DataRow(4, 23)]
    [DataRow(8, 27)]
    public void TruncatedFlushPayloadFailsClosed(int pointerSize, int truncatedLength)
    {
        var payload = new byte[truncatedLength];

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoEventDecoder.TryDecodeCompletion(
                WindowsDiskIoEventDecoder.DiskIoProviderId,
                WindowsDiskIoEventDecoder.FlushEventType,
                pointerSize,
                payload,
                out _));
    }

    [TestMethod]
    public void AdditionalTrailingPayloadBytesDoNotShiftDocumentedFields()
    {
        var payload = CreateReadWritePayload(8, 5, 6, 7, 8, 9, 10, trailingBytes: 16);
        payload.AsSpan(payload.Length - 16).Fill(0xEE);

        Assert.IsTrue(WindowsDiskIoEventDecoder.TryDecodeCompletion(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            WindowsDiskIoEventDecoder.ReadEventType,
            8,
            payload,
            out var completion));
        Assert.IsNotNull(completion);
        Assert.AreEqual(5u, completion.PhysicalDiskNumber);
        Assert.AreEqual(7L, completion.TransferBytes);
        Assert.AreEqual(9UL, completion.HighResolutionResponseTicks);
        Assert.AreEqual(10u, completion.IssuingThreadId);
    }

    private static byte[] CreateReadWritePayload(
        int pointerSize,
        uint diskNumber,
        uint irpFlags,
        uint transferSize,
        long byteOffset,
        ulong highResolutionResponseTicks,
        uint issuingThreadId,
        int trailingBytes = 0)
    {
        var highResolutionOffset = 24 + (2 * pointerSize);
        var issuingThreadOffset = highResolutionOffset + sizeof(ulong);
        var payload = new byte[issuingThreadOffset + sizeof(uint) + trailingBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), diskNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), irpFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), transferSize);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), 0xDEADBEEFu);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16, 8), byteOffset);
        FillPointer(payload.AsSpan(24, pointerSize), pointerSize, 0x11111111u);
        FillPointer(payload.AsSpan(24 + pointerSize, pointerSize), pointerSize, 0x22222222u);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(highResolutionOffset, 8),
            highResolutionResponseTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(issuingThreadOffset, 4),
            issuingThreadId);
        return payload;
    }

    private static byte[] CreateFlushPayload(
        int pointerSize,
        uint diskNumber,
        uint irpFlags,
        ulong highResolutionResponseTicks,
        uint issuingThreadId)
    {
        var issuingThreadOffset = 16 + pointerSize;
        var payload = new byte[issuingThreadOffset + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), diskNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), irpFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), highResolutionResponseTicks);
        FillPointer(payload.AsSpan(16, pointerSize), pointerSize, 0x33333333u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(issuingThreadOffset, 4),
            issuingThreadId);
        return payload;
    }

    private static void FillPointer(Span<byte> destination, int pointerSize, uint marker)
    {
        if (pointerSize == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, marker);
            return;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, ((ulong)marker << 32) | marker);
    }
}
