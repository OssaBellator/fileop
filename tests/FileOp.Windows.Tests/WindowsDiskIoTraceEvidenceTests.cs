using System.Runtime.InteropServices;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoTraceEvidenceTests
{
    private const int UnusedConsumerEventsLostOffset32 = 396;
    private const int UnusedConsumerEventsLostOffset64 = 416;

    [TestMethod]
    public void TraceHeaderEventsLostOffsetMatchesExplicitLogfileLayout()
    {
        Assert.AreEqual(48, WindowsDiskIoTraceLogfileBuffer.TraceLogfileEventsLostRelativeOffset);
        Assert.AreEqual(
            IntPtr.Size == 8 ? 168 : 160,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfileEventsLostOffset);
    }

    [TestMethod]
    public void ReadsFrequencyAndLossCountersFromTraceLogfileHeader()
    {
        using var fixture = TraceLogfileFixture.Create();
        Marshal.WriteInt64(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfilePerfFreqOffset,
            10_000_000);
        Marshal.WriteInt32(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfileEventsLostOffset,
            7);
        Marshal.WriteInt32(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfileBuffersLostOffset,
            3);

        var evidence = WindowsDiskIoTraceEvidenceReader.Read(fixture.Buffer);

        Assert.AreEqual(10_000_000L, evidence.PerformanceCounterFrequency);
        Assert.AreEqual(7u, evidence.EventsLost);
        Assert.AreEqual(3u, evidence.BuffersLost);
        Assert.IsTrue(evidence.HasValidPerformanceCounterFrequency);
        Assert.IsTrue(evidence.HasReportedLoss);
    }

    [TestMethod]
    public void UnusedConsumerEventsLostFieldDoesNotBecomeEvidence()
    {
        using var fixture = TraceLogfileFixture.Create();
        var unusedConsumerOffset = IntPtr.Size == 8
            ? UnusedConsumerEventsLostOffset64
            : UnusedConsumerEventsLostOffset32;
        Marshal.WriteInt64(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfilePerfFreqOffset,
            10_000_000);
        Marshal.WriteInt32(fixture.Buffer.Pointer, unusedConsumerOffset, 99);

        var evidence = WindowsDiskIoTraceEvidenceReader.Read(fixture.Buffer);

        Assert.AreEqual(0u, evidence.EventsLost);
        Assert.AreEqual(0u, evidence.BuffersLost);
        Assert.IsFalse(evidence.HasReportedLoss);
    }

    [TestMethod]
    public void ZeroFrequencyRemainsExplicitlyInvalidWithoutInventingFallbackClock()
    {
        using var fixture = TraceLogfileFixture.Create();

        var evidence = WindowsDiskIoTraceEvidenceReader.Read(fixture.Buffer);

        Assert.AreEqual(0L, evidence.PerformanceCounterFrequency);
        Assert.IsFalse(evidence.HasValidPerformanceCounterFrequency);
        Assert.IsFalse(evidence.HasReportedLoss);
    }

    [TestMethod]
    public void LossCountersPreserveFullUnsignedRange()
    {
        using var fixture = TraceLogfileFixture.Create();
        Marshal.WriteInt64(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfilePerfFreqOffset,
            1);
        Marshal.WriteInt32(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfileEventsLostOffset,
            -1);
        Marshal.WriteInt32(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfileBuffersLostOffset,
            int.MinValue);

        var evidence = WindowsDiskIoTraceEvidenceReader.Read(fixture.Buffer);

        Assert.AreEqual(uint.MaxValue, evidence.EventsLost);
        Assert.AreEqual(0x80000000u, evidence.BuffersLost);
        Assert.IsTrue(evidence.HasReportedLoss);
    }

    [TestMethod]
    public void EventAndBufferLossRemainSeparateEvidence()
    {
        var onlyEvents = new WindowsDiskIoTraceEvidence(10_000_000, 9, 0);
        var onlyBuffers = new WindowsDiskIoTraceEvidence(10_000_000, 0, 4);

        Assert.AreEqual(9u, onlyEvents.EventsLost);
        Assert.AreEqual(0u, onlyEvents.BuffersLost);
        Assert.IsTrue(onlyEvents.HasReportedLoss);
        Assert.AreEqual(0u, onlyBuffers.EventsLost);
        Assert.AreEqual(4u, onlyBuffers.BuffersLost);
        Assert.IsTrue(onlyBuffers.HasReportedLoss);
    }

    private sealed class TraceLogfileFixture : IDisposable
    {
        private IntPtr _loggerName;

        private TraceLogfileFixture(
            WindowsDiskIoTraceLogfileBuffer buffer,
            IntPtr loggerName)
        {
            Buffer = buffer;
            _loggerName = loggerName;
        }

        public WindowsDiskIoTraceLogfileBuffer Buffer { get; }

        public static TraceLogfileFixture Create()
        {
            var loggerName = Marshal.StringToHGlobalUni("FileOp evidence test");
            try
            {
                var buffer = WindowsDiskIoTraceLogfileBuffer.CreateForRealtimeOpen(
                    loggerName,
                    WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode,
                    new IntPtr(1),
                    new IntPtr(2),
                    IntPtr.Zero);
                return new TraceLogfileFixture(buffer, loggerName);
            }
            catch
            {
                Marshal.FreeHGlobal(loggerName);
                throw;
            }
        }

        public void Dispose()
        {
            Buffer.Dispose();
            var loggerName = Interlocked.Exchange(ref _loggerName, IntPtr.Zero);
            if (loggerName != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(loggerName);
            }
        }
    }
}
