using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoEventRecordBridgeTests
{
    [TestMethod]
    public void Classic32BitReadUsesOpcodeAndExistingDecoder()
    {
        var payload = CreateReadWritePayload(
            pointerSize: 4,
            diskNumber: 7,
            irpFlags: 0x12345678,
            transferSize: 8192,
            byteOffset: 123456789,
            responseTicks: 25_000,
            issuingThreadId: 555);
        using var fixture = EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventId: 0xBEEF,
            opcode: WindowsDiskIoEventDecoder.ReadEventType,
            flags: WindowsDiskIoEventRecordBridge.EventHeaderFlagClassic |
                   WindowsDiskIoEventRecordBridge.EventHeaderFlag32Bit,
            timestamp: 1234,
            processorIndex: 9,
            loggerId: 10,
            payload);
        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.Pointer);

        Assert.IsTrue(WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
            snapshot,
            performanceCounterFrequency: 10_000_000,
            out var decoded));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(1234L, decoded.EventTimestamp);
        Assert.AreEqual((ushort)9, decoded.ProcessorIndex);
        Assert.AreEqual((ushort)10, decoded.LoggerId);
        Assert.AreEqual(7u, decoded.Completion.PhysicalDiskNumber);
        Assert.AreEqual(DiskIoOperationKind.Read, decoded.Completion.Operation);
        Assert.AreEqual(8192L, decoded.Completion.TransferBytes);
        Assert.AreEqual(0x12345678u, decoded.Completion.IrpFlags);
        Assert.AreEqual(123456789L, decoded.Completion.ByteOffset);
        Assert.AreEqual(555u, decoded.Completion.IssuingThreadId);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2.5), decoded.ResponseTime);
    }

    [TestMethod]
    public void Classic64BitWriteUsesPayloadPointerWidth()
    {
        var payload = CreateReadWritePayload(
            pointerSize: 8,
            diskNumber: 3,
            irpFlags: 77,
            transferSize: 1_048_576,
            byteOffset: -4096,
            responseTicks: 5_000,
            issuingThreadId: uint.MaxValue);
        using var fixture = EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventId: 0,
            opcode: WindowsDiskIoEventDecoder.WriteEventType,
            flags: WindowsDiskIoEventRecordBridge.EventHeaderFlagClassic |
                   WindowsDiskIoEventRecordBridge.EventHeaderFlag64Bit,
            timestamp: long.MaxValue,
            processorIndex: ushort.MaxValue,
            loggerId: 0,
            payload);
        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.Pointer);

        Assert.IsTrue(WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
            snapshot,
            performanceCounterFrequency: 1_000_000,
            out var decoded));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(DiskIoOperationKind.Write, decoded.Completion.Operation);
        Assert.AreEqual(1_048_576L, decoded.Completion.TransferBytes);
        Assert.AreEqual(-4096L, decoded.Completion.ByteOffset);
        Assert.AreEqual(uint.MaxValue, decoded.Completion.IssuingThreadId);
        Assert.AreEqual(TimeSpan.FromMilliseconds(5), decoded.ResponseTime);
    }

    [TestMethod]
    public void ClassicFlushKeepsZeroTransferBytes()
    {
        var payload = CreateFlushPayload(
            pointerSize: 8,
            diskNumber: 12,
            irpFlags: 9,
            responseTicks: 10,
            issuingThreadId: 44);
        using var fixture = EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventId: 999,
            opcode: WindowsDiskIoEventDecoder.FlushEventType,
            flags: WindowsDiskIoEventRecordBridge.EventHeaderFlagClassic |
                   WindowsDiskIoEventRecordBridge.EventHeaderFlag64Bit,
            timestamp: 88,
            processorIndex: 1,
            loggerId: 2,
            payload);
        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.Pointer);

        Assert.IsTrue(WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
            snapshot,
            performanceCounterFrequency: 10_000_000,
            out var decoded));

        Assert.IsNotNull(decoded);
        Assert.AreEqual(DiskIoOperationKind.Flush, decoded.Completion.Operation);
        Assert.AreEqual(0L, decoded.Completion.TransferBytes);
        Assert.IsNull(decoded.Completion.ByteOffset);
        Assert.AreEqual(TimeSpan.FromTicks(10), decoded.ResponseTime);
    }

    [TestMethod]
    public void UnrelatedProviderOrOpcodeIsIgnoredBeforeMetadataValidation()
    {
        using var wrongProvider = EventRecordFixture.Create(
            Guid.NewGuid(),
            eventId: 0,
            opcode: WindowsDiskIoEventDecoder.ReadEventType,
            flags: 0,
            timestamp: 0,
            processorIndex: 0,
            loggerId: 0,
            payload: Array.Empty<byte>());
        var wrongProviderSnapshot = WindowsEtwEventRecordSnapshot.CopyFrom(wrongProvider.Pointer);

        Assert.IsFalse(WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
            wrongProviderSnapshot,
            performanceCounterFrequency: 0,
            out var wrongProviderDecoded));
        Assert.IsNull(wrongProviderDecoded);

        using var wrongOpcode = EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventId: WindowsDiskIoEventDecoder.ReadEventType,
            opcode: 99,
            flags: 0,
            timestamp: 0,
            processorIndex: 0,
            loggerId: 0,
            payload: Array.Empty<byte>());
        var wrongOpcodeSnapshot = WindowsEtwEventRecordSnapshot.CopyFrom(wrongOpcode.Pointer);

        Assert.IsFalse(WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
            wrongOpcodeSnapshot,
            performanceCounterFrequency: 0,
            out var wrongOpcodeDecoded));
        Assert.IsNull(wrongOpcodeDecoded);
    }

    [TestMethod]
    public void DiskIoIdentityUsesOpcodeNotEventId()
    {
        var payload = CreateFlushPayload(4, 1, 0, 1, 2);
        using var fixture = EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventId: 0,
            opcode: WindowsDiskIoEventDecoder.FlushEventType,
            flags: WindowsDiskIoEventRecordBridge.EventHeaderFlagClassic |
                   WindowsDiskIoEventRecordBridge.EventHeaderFlag32Bit,
            timestamp: 0,
            processorIndex: 0,
            loggerId: 0,
            payload);
        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.Pointer);

        Assert.IsTrue(WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
            snapshot,
            TimeSpan.TicksPerSecond,
            out var decoded));
        Assert.IsNotNull(decoded);
        Assert.AreEqual(DiskIoOperationKind.Flush, decoded.Completion.Operation);
    }

    [TestMethod]
    public void TargetCompletionRequiresClassicHeader()
    {
        var payload = CreateFlushPayload(4, 1, 0, 1, 2);
        using var fixture = EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventId: 0,
            opcode: WindowsDiskIoEventDecoder.FlushEventType,
            flags: WindowsDiskIoEventRecordBridge.EventHeaderFlag32Bit,
            timestamp: 0,
            processorIndex: 0,
            loggerId: 0,
            payload);
        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.Pointer);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
                snapshot,
                TimeSpan.TicksPerSecond,
                out _));
    }

    [TestMethod]
    public void TargetCompletionRequiresExactlyOnePointerWidthFlag()
    {
        foreach (var pointerFlags in new ushort[]
        {
            0,
            WindowsDiskIoEventRecordBridge.EventHeaderFlag32Bit |
            WindowsDiskIoEventRecordBridge.EventHeaderFlag64Bit,
        })
        {
            var payload = CreateFlushPayload(4, 1, 0, 1, 2);
            using var fixture = EventRecordFixture.Create(
                WindowsDiskIoEventDecoder.DiskIoProviderId,
                eventId: 0,
                opcode: WindowsDiskIoEventDecoder.FlushEventType,
                flags: (ushort)(WindowsDiskIoEventRecordBridge.EventHeaderFlagClassic | pointerFlags),
                timestamp: 0,
                processorIndex: 0,
                loggerId: 0,
                payload);
            var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.Pointer);

            Assert.ThrowsException<InvalidDataException>(() =>
                WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
                    snapshot,
                    TimeSpan.TicksPerSecond,
                    out _));
        }
    }

    [TestMethod]
    public void TargetCompletionPropagatesTruncatedPayloadFailure()
    {
        using var fixture = EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventId: 0,
            opcode: WindowsDiskIoEventDecoder.ReadEventType,
            flags: WindowsDiskIoEventRecordBridge.EventHeaderFlagClassic |
                   WindowsDiskIoEventRecordBridge.EventHeaderFlag64Bit,
            timestamp: 0,
            processorIndex: 0,
            loggerId: 0,
            payload: new byte[51]);
        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.Pointer);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
                snapshot,
                TimeSpan.TicksPerSecond,
                out _));
    }

    [TestMethod]
    public void TargetCompletionRequiresPositivePerfFrequency()
    {
        var payload = CreateFlushPayload(8, 1, 0, 1, 2);
        using var fixture = EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            eventId: 0,
            opcode: WindowsDiskIoEventDecoder.FlushEventType,
            flags: WindowsDiskIoEventRecordBridge.EventHeaderFlagClassic |
                   WindowsDiskIoEventRecordBridge.EventHeaderFlag64Bit,
            timestamp: 0,
            processorIndex: 0,
            loggerId: 0,
            payload);
        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.Pointer);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
                snapshot,
                performanceCounterFrequency: 0,
                out _));
    }

    private static byte[] CreateReadWritePayload(
        int pointerSize,
        uint diskNumber,
        uint irpFlags,
        uint transferSize,
        long byteOffset,
        ulong responseTicks,
        uint issuingThreadId)
    {
        var highResolutionOffset = 24 + (2 * pointerSize);
        var issuingThreadOffset = highResolutionOffset + sizeof(ulong);
        var payload = new byte[issuingThreadOffset + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), diskNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), irpFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), transferSize);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16, 8), byteOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(highResolutionOffset, 8), responseTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(issuingThreadOffset, 4), issuingThreadId);
        return payload;
    }

    private static byte[] CreateFlushPayload(
        int pointerSize,
        uint diskNumber,
        uint irpFlags,
        ulong responseTicks,
        uint issuingThreadId)
    {
        var issuingThreadOffset = 16 + pointerSize;
        var payload = new byte[issuingThreadOffset + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), diskNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), irpFlags);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8, 8), responseTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(issuingThreadOffset, 4), issuingThreadId);
        return payload;
    }

    private sealed class EventRecordFixture : IDisposable
    {
        private IntPtr _record;
        private IntPtr _payload;

        private EventRecordFixture(IntPtr record, IntPtr payload)
        {
            _record = record;
            _payload = payload;
        }

        public IntPtr Pointer => _record;

        public static EventRecordFixture Create(
            Guid provider,
            ushort eventId,
            byte opcode,
            ushort flags,
            long timestamp,
            ushort processorIndex,
            ushort loggerId,
            byte[] payload)
        {
            var fixedSize = IntPtr.Size == 8
                ? WindowsEtwEventRecordSnapshot.EventRecordFixedSize64
                : WindowsEtwEventRecordSnapshot.EventRecordFixedSize32;
            var record = Marshal.AllocHGlobal(fixedSize);
            Marshal.Copy(new byte[fixedSize], 0, record, fixedSize);
            var payloadMemory = IntPtr.Zero;
            try
            {
                if (payload.Length > 0)
                {
                    payloadMemory = Marshal.AllocHGlobal(payload.Length);
                    Marshal.Copy(payload, 0, payloadMemory, payload.Length);
                }

                WriteUInt16(record, 0, checked((ushort)(WindowsEtwEventRecordSnapshot.EventHeaderSizeBytes + payload.Length)));
                WriteUInt16(record, 4, flags);
                Marshal.WriteInt64(record, 16, timestamp);
                WriteGuid(record, 24, provider);
                WriteUInt16(record, 40, eventId);
                Marshal.WriteByte(record, 45, opcode);
                WriteUInt16(record, WindowsEtwEventRecordSnapshot.BufferContextOffset, processorIndex);
                WriteUInt16(record, WindowsEtwEventRecordSnapshot.BufferContextOffset + 2, loggerId);
                WriteUInt16(record, WindowsEtwEventRecordSnapshot.UserDataLengthOffset, checked((ushort)payload.Length));
                Marshal.WriteIntPtr(
                    record,
                    IntPtr.Size == 8
                        ? WindowsEtwEventRecordSnapshot.UserDataPointerOffset64
                        : WindowsEtwEventRecordSnapshot.UserDataPointerOffset32,
                    payloadMemory);
                return new EventRecordFixture(record, payloadMemory);
            }
            catch
            {
                if (payloadMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(payloadMemory);
                }
                Marshal.FreeHGlobal(record);
                throw;
            }
        }

        private static void WriteUInt16(IntPtr pointer, int offset, ushort value) =>
            Marshal.WriteInt16(pointer, offset, unchecked((short)value));

        private static void WriteGuid(IntPtr pointer, int offset, Guid value)
        {
            var bytes = value.ToByteArray();
            Marshal.Copy(bytes, 0, IntPtr.Add(pointer, offset), bytes.Length);
        }

        public void Dispose()
        {
            var payload = Interlocked.Exchange(ref _payload, IntPtr.Zero);
            if (payload != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(payload);
            }

            var record = Interlocked.Exchange(ref _record, IntPtr.Zero);
            if (record != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(record);
            }
        }
    }
}
