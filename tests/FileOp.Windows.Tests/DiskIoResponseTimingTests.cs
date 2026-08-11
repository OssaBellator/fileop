using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoResponseTimingTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 11, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddSeconds(2);

    [TestMethod]
    public void SummarizesEachOperationPerPhysicalDisk()
    {
        var observations = new[]
        {
            Timing(0, DiskIoOperationKind.Read, 5, 1),
            Timing(0, DiskIoOperationKind.Read, 1, 2),
            Timing(0, DiskIoOperationKind.Read, 3, 3),
            Timing(0, DiskIoOperationKind.Read, 2, 4),
            Timing(0, DiskIoOperationKind.Read, 4, 5),
            Timing(0, DiskIoOperationKind.Write, 2, 6),
            Timing(0, DiskIoOperationKind.Write, 8, 7),
            Timing(0, DiskIoOperationKind.Flush, 6, 8),
            Timing(2, DiskIoOperationKind.Read, 7, 9),
        };

        var result = DiskIoResponseTimingAnalyzer.Analyze(StartedAt, EndedAt, observations);

        Assert.AreEqual(2, result.Count);
        var disk0 = result[0];
        Assert.AreEqual(0u, disk0.PhysicalDiskNumber);
        Assert.AreEqual(8, disk0.SampleCount);
        AssertSummary(
            disk0.Reads,
            count: 5,
            minimumMilliseconds: 1,
            medianMilliseconds: 3,
            p95Milliseconds: 5,
            maximumMilliseconds: 5);
        AssertSummary(
            disk0.Writes,
            count: 2,
            minimumMilliseconds: 2,
            medianMilliseconds: 5,
            p95Milliseconds: null,
            maximumMilliseconds: 8);
        AssertSummary(
            disk0.Flushes,
            count: 1,
            minimumMilliseconds: 6,
            medianMilliseconds: 6,
            p95Milliseconds: null,
            maximumMilliseconds: 6);

        var disk2 = result[1];
        Assert.AreEqual(2u, disk2.PhysicalDiskNumber);
        Assert.AreEqual(1, disk2.SampleCount);
        Assert.IsNotNull(disk2.Reads);
        Assert.IsNull(disk2.Writes);
        Assert.IsNull(disk2.Flushes);
    }

    [TestMethod]
    public void EmptyTimingSetRemainsEmptyInsteadOfInventingLatency()
    {
        var result = DiskIoResponseTimingAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            Array.Empty<DiskIoResponseTimingObservation>());

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void InvalidTimingEvidenceFailsClosed()
    {
        Assert.ThrowsException<InvalidDataException>(() =>
            DiskIoResponseTimingAnalyzer.Analyze(
                StartedAt,
                EndedAt,
                [new DiskIoResponseTimingObservation(
                    StartedAt.AddMilliseconds(1),
                    0,
                    DiskIoOperationKind.Read,
                    TimeSpan.FromTicks(-1))]));

        Assert.ThrowsException<InvalidDataException>(() =>
            DiskIoResponseTimingAnalyzer.Analyze(
                StartedAt,
                EndedAt,
                [new DiskIoResponseTimingObservation(
                    EndedAt.AddTicks(1),
                    0,
                    DiskIoOperationKind.Read,
                    TimeSpan.Zero)]));
    }

    [TestMethod]
    public void CaptureResultKeepsLegacyEmptyTimingEvidenceByDefault()
    {
        var report = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            Array.Empty<DiskIoEventObservation>());
        var result = DiskIoCaptureResult.Completed(
            new DiskIoCaptureBudget(TimeSpan.FromSeconds(2), 10, DiskIoAttributionAnalyzer.DefaultMaxOwnersPerDisk),
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            lostEventCount: 0,
            lostBufferCount: 0,
            providerOverheadDuration: null,
            detail: "test");

        Assert.AreEqual(0, result.ResponseTimings.Count);
    }

    private static DiskIoResponseTimingObservation Timing(
        uint disk,
        DiskIoOperationKind operation,
        int responseMilliseconds,
        int timestampMilliseconds) =>
        new(
            StartedAt.AddMilliseconds(timestampMilliseconds),
            disk,
            operation,
            TimeSpan.FromMilliseconds(responseMilliseconds));

    private static void AssertSummary(
        DiskIoResponseTimingSummary? summary,
        int count,
        int minimumMilliseconds,
        int medianMilliseconds,
        int? p95Milliseconds,
        int maximumMilliseconds)
    {
        Assert.IsNotNull(summary);
        Assert.AreEqual(count, summary.SampleCount);
        Assert.AreEqual(TimeSpan.FromMilliseconds(minimumMilliseconds), summary.Minimum);
        Assert.AreEqual(TimeSpan.FromMilliseconds(medianMilliseconds), summary.Median);
        Assert.AreEqual(
            p95Milliseconds is { } p95 ? TimeSpan.FromMilliseconds(p95) : null,
            summary.P95);
        Assert.AreEqual(TimeSpan.FromMilliseconds(maximumMilliseconds), summary.Maximum);
    }
}
