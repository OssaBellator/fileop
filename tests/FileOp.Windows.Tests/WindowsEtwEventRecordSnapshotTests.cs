using System.Runtime.InteropServices;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsEtwEventRecordSnapshotTests
{
    [TestMethod]
    public void CopiesHeaderDescriptorContextAndUserDataIntoOwnedValues()
    {
        var provider = Guid.Parse("3d5c43e3-0f1c-4202-b817-174c0070dc79");
        var activity = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var payload = new byte[] { 1, 2, 3, 4, 5, 6 };
        using var fixture = NativeEventRecordFixture.Create(payload);
        fixture.WriteHeader(
            recordSize: checked((ushort)(WindowsEtwEventRecordSnapshot.EventHeaderSizeBytes + payload.Length)),
            headerType: 0x1234,
            flags: 0x0060,
            eventProperty: 0x0102,
            threadId: 0xAABBCCDD,
            processId: 0x11223344,
            timestamp: 0x0102030405060708,
            provider,
            descriptor: new WindowsEtwEventDescriptorSnapshot(
                0xABCD,
                7,
                8,
                9,
                10,
                0xBCDE,
                0x1122334455667788),
            processorTime: 0x8877665544332211,
            activity,
            processorIndex: 0x1234,
            loggerId: 0x5678,
            extendedDataCount: 3);
        fixture.WriteExtendedDataPointer(new IntPtr(0x123456));

        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.RecordPointer);

        Assert.AreEqual((ushort)86, snapshot.EventRecordSize);
        Assert.AreEqual((ushort)0x1234, snapshot.HeaderType);
        Assert.AreEqual((ushort)0x0060, snapshot.Flags);
        Assert.AreEqual((ushort)0x0102, snapshot.EventProperty);
        Assert.AreEqual(0xAABBCCDDu, snapshot.ThreadId);
        Assert.AreEqual(0x11223344u, snapshot.ProcessId);
        Assert.AreEqual(0x0102030405060708L, snapshot.Timestamp);
        Assert.AreEqual(provider, snapshot.ProviderId);
        Assert.AreEqual((ushort)0xABCD, snapshot.Descriptor.Id);
        Assert.AreEqual((byte)7, snapshot.Descriptor.Version);
        Assert.AreEqual((byte)8, snapshot.Descriptor.Channel);
        Assert.AreEqual((byte)9, snapshot.Descriptor.Level);
        Assert.AreEqual((byte)10, snapshot.Descriptor.Opcode);
        Assert.AreEqual((ushort)0xBCDE, snapshot.Descriptor.Task);
        Assert.AreEqual(0x1122334455667788UL, snapshot.Descriptor.Keyword);
        Assert.AreEqual(0x8877665544332211UL, snapshot.ProcessorTime);
        Assert.AreEqual(activity, snapshot.ActivityId);
        Assert.AreEqual((ushort)0x1234, snapshot.ProcessorIndex);
        Assert.AreEqual((ushort)0x5678, snapshot.LoggerId);
        Assert.AreEqual((ushort)3, snapshot.ExtendedDataCount);
        CollectionAssert.AreEqual(payload, snapshot.UserData.ToArray());
    }

    [TestMethod]
    public void SnapshotSurvivesMutationOfNativeRecordAndPayload()
    {
        var payload = new byte[] { 10, 20, 30, 40 };
        using var fixture = NativeEventRecordFixture.Create(payload);
        fixture.WriteHeader(
            recordSize: 84,
            headerType: 1,
            flags: 2,
            eventProperty: 3,
            threadId: 4,
            processId: 5,
            timestamp: 6,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            new WindowsEtwEventDescriptorSnapshot(7, 8, 9, 10, 11, 12, 13),
            processorTime: 14,
            Guid.Parse("99999999-8888-7777-6666-555555555555"),
            processorIndex: 15,
            loggerId: 16,
            extendedDataCount: 0);

        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.RecordPointer);
        fixture.OverwritePayload(new byte[] { 99, 98, 97, 96 });
        Marshal.WriteInt32(fixture.RecordPointer, 8, unchecked((int)0xFFFFFFFF));

        Assert.AreEqual(4u, snapshot.ThreadId);
        CollectionAssert.AreEqual(payload, snapshot.UserData.ToArray());
    }

    [TestMethod]
    public void ZeroLengthPayloadAllowsNullUserDataPointer()
    {
        using var fixture = NativeEventRecordFixture.Create(Array.Empty<byte>());
        fixture.WriteHeader(recordSize: 80);
        fixture.WriteUserDataPointer(IntPtr.Zero);

        var snapshot = WindowsEtwEventRecordSnapshot.CopyFrom(fixture.RecordPointer);

        Assert.AreEqual(0, snapshot.UserDataLength);
        Assert.IsTrue(snapshot.UserData.IsEmpty);
    }

    [TestMethod]
    public void NonzeroPayloadWithNullPointerFailsClosed()
    {
        using var fixture = NativeEventRecordFixture.Create(new byte[] { 1, 2 });
        fixture.WriteHeader(recordSize: 82);
        fixture.WriteUserDataPointer(IntPtr.Zero);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsEtwEventRecordSnapshot.CopyFrom(fixture.RecordPointer));
    }

    [TestMethod]
    public void CopyLimitIsEnforcedBeforePayloadDereference()
    {
        using var fixture = NativeEventRecordFixture.Create(new byte[] { 1, 2, 3, 4 });
        fixture.WriteHeader(recordSize: 84);
        fixture.WriteUserDataPointer(new IntPtr(1));

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsEtwEventRecordSnapshot.CopyFrom(
                fixture.RecordPointer,
                maximumUserDataBytes: 3));
    }

    [TestMethod]
    public void InvalidHeaderSizeFailsClosed()
    {
        using var fixture = NativeEventRecordFixture.Create(Array.Empty<byte>());
        fixture.WriteHeader(recordSize: 79);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsEtwEventRecordSnapshot.CopyFrom(fixture.RecordPointer));
    }

    [TestMethod]
    public void EventSizeMustBeLargeEnoughForDeclaredPayload()
    {
        using var fixture = NativeEventRecordFixture.Create(new byte[] { 1, 2, 3, 4 });
        fixture.WriteHeader(recordSize: 82);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsEtwEventRecordSnapshot.CopyFrom(fixture.RecordPointer));
    }

    [TestMethod]
    public void CopyLimitArgumentMustFitNativeUserDataLengthRange()
    {
        using var fixture = NativeEventRecordFixture.Create(Array.Empty<byte>());
        fixture.WriteHeader(recordSize: 80);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            WindowsEtwEventRecordSnapshot.CopyFrom(fixture.RecordPointer, -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            WindowsEtwEventRecordSnapshot.CopyFrom(fixture.RecordPointer, ushort.MaxValue + 1));
    }

    [TestMethod]
    public void NullEventRecordPointerIsRejected()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            WindowsEtwEventRecordSnapshot.CopyFrom(IntPtr.Zero));
    }

    private sealed class NativeEventRecordFixture : IDisposable
    {
        private IntPtr _record;
        private IntPtr _payload;

        private NativeEventRecordFixture(IntPtr record, IntPtr payload, int payloadLength)
        {
            _record = record;
            _payload = payload;
            PayloadLength = payloadLength;
        }

        public IntPtr RecordPointer => _record;

        public int PayloadLength { get; }

        public static NativeEventRecordFixture Create(byte[] payload)
        {
            var fixedSize = IntPtr.Size == 8
                ? WindowsEtwEventRecordSnapshot.EventRecordFixedSize64
                : WindowsEtwEventRecordSnapshot.EventRecordFixedSize32;
            var record = Marshal.AllocHGlobal(fixedSize);
            Marshal.Copy(new byte[fixedSize], 0, record, fixedSize);

            var payloadPointer = IntPtr.Zero;
            try
            {
                if (payload.Length > 0)
                {
                    payloadPointer = Marshal.AllocHGlobal(payload.Length);
                    Marshal.Copy(payload, 0, payloadPointer, payload.Length);
                }

                var fixture = new NativeEventRecordFixture(record, payloadPointer, payload.Length);
                fixture.WriteUserDataLength(checked((ushort)payload.Length));
                fixture.WriteUserDataPointer(payloadPointer);
                return fixture;
            }
            catch
            {
                if (payloadPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(payloadPointer);
                }
                Marshal.FreeHGlobal(record);
                throw;
            }
        }

        public void WriteHeader(
            ushort recordSize,
            ushort headerType = 0,
            ushort flags = 0,
            ushort eventProperty = 0,
            uint threadId = 0,
            uint processId = 0,
            long timestamp = 0,
            Guid provider = default,
            WindowsEtwEventDescriptorSnapshot descriptor = default,
            ulong processorTime = 0,
            Guid activity = default,
            ushort processorIndex = 0,
            ushort loggerId = 0,
            ushort extendedDataCount = 0)
        {
            WriteUInt16(0, recordSize);
            WriteUInt16(2, headerType);
            WriteUInt16(4, flags);
            WriteUInt16(6, eventProperty);
            WriteUInt32(8, threadId);
            WriteUInt32(12, processId);
            Marshal.WriteInt64(_record, 16, timestamp);
            WriteGuid(24, provider);
            WriteUInt16(40, descriptor.Id);
            Marshal.WriteByte(_record, 42, descriptor.Version);
            Marshal.WriteByte(_record, 43, descriptor.Channel);
            Marshal.WriteByte(_record, 44, descriptor.Level);
            Marshal.WriteByte(_record, 45, descriptor.Opcode);
            WriteUInt16(46, descriptor.Task);
            Marshal.WriteInt64(_record, 48, unchecked((long)descriptor.Keyword));
            Marshal.WriteInt64(_record, 56, unchecked((long)processorTime));
            WriteGuid(64, activity);
            WriteUInt16(WindowsEtwEventRecordSnapshot.BufferContextOffset, processorIndex);
            WriteUInt16(WindowsEtwEventRecordSnapshot.BufferContextOffset + sizeof(ushort), loggerId);
            WriteUInt16(WindowsEtwEventRecordSnapshot.ExtendedDataCountOffset, extendedDataCount);
        }

        public void WriteExtendedDataPointer(IntPtr pointer) =>
            Marshal.WriteIntPtr(
                _record,
                WindowsEtwEventRecordSnapshot.ExtendedDataPointerOffset,
                pointer);

        public void WriteUserDataPointer(IntPtr pointer) =>
            Marshal.WriteIntPtr(
                _record,
                IntPtr.Size == 8
                    ? WindowsEtwEventRecordSnapshot.UserDataPointerOffset64
                    : WindowsEtwEventRecordSnapshot.UserDataPointerOffset32,
                pointer);

        public void OverwritePayload(byte[] payload)
        {
            if (payload.Length != PayloadLength || _payload == IntPtr.Zero)
            {
                throw new ArgumentException("Payload replacement must match the allocated fixture size.");
            }

            Marshal.Copy(payload, 0, _payload, payload.Length);
        }

        private void WriteUserDataLength(ushort value) =>
            WriteUInt16(WindowsEtwEventRecordSnapshot.UserDataLengthOffset, value);

        private void WriteUInt16(int offset, ushort value) =>
            Marshal.WriteInt16(_record, offset, unchecked((short)value));

        private void WriteUInt32(int offset, uint value) =>
            Marshal.WriteInt32(_record, offset, unchecked((int)value));

        private void WriteGuid(int offset, Guid value)
        {
            var bytes = value.ToByteArray();
            Marshal.Copy(bytes, 0, IntPtr.Add(_record, offset), bytes.Length);
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
