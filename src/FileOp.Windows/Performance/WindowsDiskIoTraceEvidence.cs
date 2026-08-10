using System.Runtime.InteropServices;

namespace FileOp.Windows.Performance;

internal sealed record WindowsDiskIoTraceEvidence(
    long PerformanceCounterFrequency,
    uint EventsLost,
    uint BuffersLost)
{
    public bool HasValidPerformanceCounterFrequency =>
        PerformanceCounterFrequency > 0;

    public bool HasReportedLoss =>
        EventsLost != 0 || BuffersLost != 0;
}

internal interface IWindowsDiskIoTraceEvidenceSource
{
    WindowsDiskIoTraceEvidence ReadTraceEvidence(ulong processingHandle);
}

internal static class WindowsDiskIoTraceEvidenceReader
{
    internal const int ConsumerEventsLostOffset32 = 396;
    internal const int ConsumerEventsLostOffset64 = 416;

    internal static int ConsumerEventsLostOffset =>
        IntPtr.Size == 8
            ? ConsumerEventsLostOffset64
            : ConsumerEventsLostOffset32;

    public static WindowsDiskIoTraceEvidence Read(
        WindowsDiskIoTraceLogfileBuffer logfile)
    {
        ArgumentNullException.ThrowIfNull(logfile);

        return new WindowsDiskIoTraceEvidence(
            PerformanceCounterFrequency: Marshal.ReadInt64(
                logfile.Pointer,
                WindowsDiskIoTraceLogfileBuffer.TraceLogfilePerfFreqOffset),
            EventsLost: unchecked((uint)Marshal.ReadInt32(
                logfile.Pointer,
                ConsumerEventsLostOffset)),
            BuffersLost: unchecked((uint)Marshal.ReadInt32(
                logfile.Pointer,
                WindowsDiskIoTraceLogfileBuffer.TraceLogfileBuffersLostOffset)));
    }
}
