using System.Runtime.InteropServices;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoTraceEvidenceTests
{
    [TestMethod]
    public void ConsumerEventsLostOffsetMatchesExplicitLogfileLayout()
    {
        Assert.AreEqual(
            IntPtr.Size == 8 ? 416 : 396,
            WindowsDiskIoTraceEvidenceReader.ConsumerEventsLostOffset);
        Assert.AreEqual(396, WindowsDiskIoTraceEvidenceReader.ConsumerEventsLostOffset32);
        Assert.AreEqual(416, WindowsDiskIoTraceEvidenceReader.ConsumerEventsLostOffset64);
    }

    [TestMethod]
    public void ReadsFrequencyAndLossCountersFromRetainedLogfileState()
    {
        using var fixture = TraceLogfileFixture.Create();
        Marshal.WriteInt64(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfilePerfFreqOffset,
            10_000_000);
        Marshal.WriteInt32(
            fixture.Buffer.Pointer,
            WindowsDiskIoTraceEvidenceReader.ConsumerEventsLostOffset,
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
            WindowsDiskIoTraceEvidenceReader.ConsumerEventsLostOffset,
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
