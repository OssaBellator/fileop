using System.Buffers.Binary;
using System.Runtime.InteropServices;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoCaptureCollectorTests
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TargetCompletionBecomesResolvedObservation()
    {
        var lifetime = new WindowsDiskIoLifetimeFake();
        lifetime.AddProcess(50, WindowStart.AddMinutes(-10), @"C:\apps\worker.exe");
        lifetime.AddThread(100, 50, WindowStart.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(lifetime);
        using var collector = new WindowsDiskIoCaptureCollector(10, resolver);
        collector.Configure(10_000_000, WindowStart, WindowStart.AddSeconds(1));
        using var record = CreateReadRecord(
            timestamp: WindowStart.AddMilliseconds(100).ToFileTime(),
            issuingThreadId: 100,
            diskNumber: 3,
            transferBytes: 8192);

        Assert.IsTrue(collector.OnEventRecord(record.Pointer));
        var snapshot = collector.Snapshot();

        Assert.AreEqual(1, snapshot.Observations.Count);
        var observation = snapshot.Observations[0];
        Assert.AreEqual(WindowStart.AddMilliseconds(100), observation.Timestamp);
        Assert.AreEqual(3u, observation.PhysicalDiskNumber);
        Assert.AreEqual(DiskIoOperationKind.Read, observation.Operation);
        Assert.AreEqual(8192L, observation.TransferBytes);
        Assert.IsNotNull(observation.Owner);
        Assert.AreEqual(50, observation.Owner.ProcessId);
        Assert.AreEqual("worker.exe", observation.Owner.ImageName);
        Assert.AreEqual(0, snapshot.UnresolvedOwnerCounts.Count);
        Assert.IsFalse(snapshot.ObservationLimitReached);
    }

    [TestMethod]
    public void UnresolvedOwnerRemainsVisibleAndReasonIsCounted()
    {
        var lifetime = new WindowsDiskIoLifetimeFake();
        using var resolver = new WindowsDiskIoIssuingThreadResolver(lifetime);
        using var collector = new WindowsDiskIoCaptureCollector(10, resolver);
        collector.Configure(10_000_000, WindowStart, WindowStart.AddSeconds(1));
        using var record = CreateReadRecord(
            WindowStart.AddMilliseconds(100).ToFileTime(),
            issuingThreadId: 999,
            diskNumber: 0,
            transferBytes: 4096);

        Assert.IsTrue(collector.OnEventRecord(record.Pointer));
        var snapshot = collector.Snapshot();

        Assert.AreEqual(1, snapshot.Observations.Count);
        Assert.IsNull(snapshot.Observations[0].Owner);
        Assert.AreEqual(
            1,
            snapshot.UnresolvedOwnerCounts[WindowsDiskIoOwnerResolutionStatus.ThreadUnavailable]);
    }

    [TestMethod]
    public void UnrelatedAndOutOfWindowEventsAreIgnored()
    {
        var lifetime = new WindowsDiskIoLifetimeFake();
        lifetime.AddProcess(50, WindowStart.AddMinutes(-10), @"C:\worker.exe");
        lifetime.AddThread(100, 50, WindowStart.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(lifetime);
        using var collector = new WindowsDiskIoCaptureCollector(10, resolver);
        collector.Configure(10_000_000, WindowStart, WindowStart.AddSeconds(1));
        using var unrelated = EventRecordFixture.Create(
            Guid.NewGuid(),
            opcode: WindowsDiskIoEventDecoder.ReadEventType,
            flags: 0,
            timestamp: WindowStart.AddMilliseconds(100).ToFileTime(),
            payload: Array.Empty<byte>());
        using var late = CreateReadRecord(
            WindowStart.AddSeconds(2).ToFileTime(),
            issuingThreadId: 100,
            diskNumber: 0,
            transferBytes: 1);

        Assert.IsTrue(collector.OnEventRecord(unrelated.Pointer));
        Assert.IsTrue(collector.OnEventRecord(late.Pointer));
        var snapshot = collector.Snapshot();

        Assert.AreEqual(0, snapshot.Observations.Count);
        Assert.AreEqual(2, snapshot.IgnoredEventCount);
        Assert.IsFalse(snapshot.ObservationLimitReached);
    }

    [TestMethod]
    public void ExactObservationLimitStopsFurtherCallbacks()
    {
        var lifetime = new WindowsDiskIoLifetimeFake();
        using var resolver = new WindowsDiskIoIssuingThreadResolver(lifetime);
        using var collector = new WindowsDiskIoCaptureCollector(2, resolver);
        collector.Configure(10_000_000, WindowStart, WindowStart.AddSeconds(1));
        using var first = CreateReadRecord(
            WindowStart.AddMilliseconds(100).ToFileTime(), 10, 0, 100);
        using var second = CreateReadRecord(
            WindowStart.AddMilliseconds(200).ToFileTime(), 11, 0, 200);

        Assert.IsTrue(collector.OnEventRecord(first.Pointer));
        Assert.IsFalse(collector.OnEventRecord(second.Pointer));
        Assert.IsFalse(collector.OnEventRecord(IntPtr.Zero));
        Assert.IsFalse(collector.OnBuffer(new IntPtr(1)));

        var snapshot = collector.Snapshot();
        Assert.AreEqual(2, snapshot.Observations.Count);
        Assert.IsTrue(snapshot.ObservationLimitReached);
        Assert.AreEqual(2, snapshot.UnresolvedOwnerCounts.Values.Sum());
    }

    [TestMethod]
    public void ConfigurationIsRequiredAndSingleShot()
    {
        using var collector = new WindowsDiskIoCaptureCollector(1);
        Assert.ThrowsException<InvalidOperationException>(() => collector.OnBuffer(new IntPtr(1)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            collector.Configure(0, WindowStart, WindowStart.AddSeconds(1)));

        collector.Configure(1, WindowStart, WindowStart.AddSeconds(1));
        Assert.ThrowsException<InvalidOperationException>(() =>
            collector.Configure(1, WindowStart, WindowStart.AddSeconds(1)));
    }

    private static EventRecordFixture CreateReadRecord(
        long timestamp,
        uint issuingThreadId,
        uint diskNumber,
        uint transferBytes)
    {
        var pointerSize = IntPtr.Size;
        var highResolutionOffset = 24 + (2 * pointerSize);
        var issuingThreadOffset = highResolutionOffset + sizeof(ulong);
        var payload = new byte[issuingThreadOffset + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), diskNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), transferBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(highResolutionOffset, 8), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(issuingThreadOffset, 4), issuingThreadId);
        var widthFlag = pointerSize == 8
            ? WindowsDiskIoEventRecordBridge.EventHeaderFlag64Bit
            : WindowsDiskIoEventRecordBridge.EventHeaderFlag32Bit;
        return EventRecordFixture.Create(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            WindowsDiskIoEventDecoder.ReadEventType,
            WindowsDiskIoEventRecordBridge.EventHeaderFlagClassic | widthFlag,
            timestamp,
            payload);
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
            byte opcode,
            ushort flags,
            long timestamp,
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

                WriteUInt16(
                    record,
                    0,
                    checked((ushort)(WindowsEtwEventRecordSnapshot.EventHeaderSizeBytes + payload.Length)));
                WriteUInt16(record, 4, flags);
                Marshal.WriteInt64(record, 16, timestamp);
                var providerBytes = provider.ToByteArray();
                Marshal.Copy(providerBytes, 0, IntPtr.Add(record, 24), providerBytes.Length);
                Marshal.WriteByte(record, 45, opcode);
                WriteUInt16(
                    record,
                    WindowsEtwEventRecordSnapshot.UserDataLengthOffset,
                    checked((ushort)payload.Length));
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
