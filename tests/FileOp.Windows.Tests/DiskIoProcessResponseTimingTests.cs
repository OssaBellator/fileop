using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoProcessResponseTimingTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 11, 4, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddSeconds(2);

    [TestMethod]
    public void PreservesByteRankedVisibleOwnersAndDisclosesHiddenAndUnattributedSamples()
    {
        var ownerA = Owner(10, "a.exe");
        var ownerB = Owner(20, "b.exe");
        var hiddenOwner = Owner(30, "hidden.exe");
        var events = new[]
        {
            Event(1, DiskIoOperationKind.Read, 1_000, ownerA),
            Event(2, DiskIoOperationKind.Read, 1_000, ownerA),
            Event(3, DiskIoOperationKind.Write, 2_000, ownerA),
            Event(4, DiskIoOperationKind.Read, 2_000, ownerB),
            Event(5, DiskIoOperationKind.Write, 100, hiddenOwner),
            Event(6, DiskIoOperationKind.Flush, 0, owner: null),
        };
        var timings = new[]
        {
            Timing(1, DiskIoOperationKind.Read, 1, ownerA),
            Timing(2, DiskIoOperationKind.Read, 3, ownerA),
            Timing(3, DiskIoOperationKind.Write, 4, ownerA),
            Timing(4, DiskIoOperationKind.Read, 500, ownerB),
            Timing(5, DiskIoOperationKind.Write, 1_000, hiddenOwner),
            Timing(6, DiskIoOperationKind.Flush, 2, owner: null),
        };
        var attribution = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            events,
            maxOwnersPerDisk: 2);

        var result = DiskIoProcessResponseTimingAnalyzer.Analyze(attribution, timings);

        Assert.AreEqual(1, result.Count);
        var disk = result[0];
        Assert.AreEqual(0u, disk.PhysicalDiskNumber);
        Assert.AreEqual(2, disk.Owners.Count);
        Assert.AreEqual(ownerA, disk.Owners[0].Owner);
        Assert.AreEqual(ownerB, disk.Owners[1].Owner);
        Assert.AreEqual(3L, disk.Owners[0].SampleCount);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), disk.Owners[0].Reads!.Median);
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), disk.Owners[1].Reads!.Maximum);
        Assert.AreEqual(1L, disk.OtherIdentifiedWriteSamples);
        Assert.AreEqual(1L, disk.UnattributedFlushSamples);
        Assert.AreEqual(6L, disk.TotalSamples);

        // The hidden owner has the largest response duration, but timing must not create
        // a latency-ranked owner row that was absent from the byte-ranked attribution report.
        Assert.IsFalse(disk.Owners.Any(owner => owner.Owner == hiddenOwner));
    }

    [TestMethod]
    public void ProcessTimingFailsClosedWhenVisibleOwnerCountsDiverge()
    {
        var ownerA = Owner(10, "a.exe");
        var ownerB = Owner(20, "b.exe");
        var events = new[]
        {
            Event(1, DiskIoOperationKind.Read, 1_000, ownerA),
            Event(2, DiskIoOperationKind.Read, 900, ownerB),
        };
        var attribution = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            events,
            maxOwnersPerDisk: 2);
        var timings = new[]
        {
            Timing(1, DiskIoOperationKind.Read, 1, ownerA),
            Timing(2, DiskIoOperationKind.Read, 2, ownerA),
        };

        Assert.Throws<InvalidDataException>(() =>
            DiskIoProcessResponseTimingAnalyzer.Analyze(attribution, timings));
    }

    [TestMethod]
    public void ProcessTimingFailsClosedWhenUnattributedAndHiddenBucketsDiverge()
    {
        var ownerA = Owner(10, "a.exe");
        var hiddenOwner = Owner(20, "hidden.exe");
        var events = new[]
        {
            Event(1, DiskIoOperationKind.Read, 1_000, ownerA),
            Event(2, DiskIoOperationKind.Write, 100, hiddenOwner),
            Event(3, DiskIoOperationKind.Flush, 0, owner: null),
        };
        var attribution = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            events,
            maxOwnersPerDisk: 1);
        var timings = new[]
        {
            Timing(1, DiskIoOperationKind.Read, 1, ownerA),
            Timing(2, DiskIoOperationKind.Write, 2, owner: null),
            Timing(3, DiskIoOperationKind.Flush, 3, owner: null),
        };

        Assert.Throws<InvalidDataException>(() =>
            DiskIoProcessResponseTimingAnalyzer.Analyze(attribution, timings));
    }

    [TestMethod]
    public void CaptureAttachmentRequiresDiskTimingAndPreservesVisibleOwnerOrder()
    {
        var ownerA = Owner(10, "a.exe");
        var ownerB = Owner(20, "b.exe");
        var events = new[]
        {
            Event(1, DiskIoOperationKind.Read, 2_000, ownerA),
            Event(2, DiskIoOperationKind.Read, 1_000, ownerB),
        };
        var timings = new[]
        {
            Timing(1, DiskIoOperationKind.Read, 1, ownerA),
            Timing(2, DiskIoOperationKind.Read, 2, ownerB),
        };
        var attribution = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            events,
            maxOwnersPerDisk: 2);
        var diskTiming = DiskIoResponseTimingAnalyzer.Analyze(StartedAt, EndedAt, timings);
        var processTiming = DiskIoProcessResponseTimingAnalyzer.Analyze(attribution, timings);
        var capture = DiskIoCaptureResult.Completed(
            new DiskIoCaptureBudget(TimeSpan.FromSeconds(2), 10, 2),
            attribution,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            lostEventCount: 0,
            lostBufferCount: 0,
            providerOverheadDuration: null,
            detail: "test");

        Assert.Throws<InvalidOperationException>(() =>
            capture.WithProcessResponseTimings(processTiming));

        var attached = capture
            .WithResponseTimings(diskTiming)
            .WithProcessResponseTimings(processTiming);
        Assert.AreEqual(1, attached.ProcessResponseTimings.Count);
        Assert.AreEqual(ownerA, attached.ProcessResponseTimings[0].Owners[0].Owner);
        Assert.AreEqual(ownerB, attached.ProcessResponseTimings[0].Owners[1].Owner);

        var reversed = new DiskIoDiskProcessResponseTiming(
            0,
            processTiming[0].Owners.Reverse().ToArray(),
            processTiming[0].UnattributedReadSamples,
            processTiming[0].UnattributedWriteSamples,
            processTiming[0].UnattributedFlushSamples,
            processTiming[0].OtherIdentifiedReadSamples,
            processTiming[0].OtherIdentifiedWriteSamples,
            processTiming[0].OtherIdentifiedFlushSamples);
        Assert.Throws<ArgumentException>(() =>
            capture.WithResponseTimings(diskTiming).WithProcessResponseTimings([reversed]));
    }

    [TestMethod]
    public void EmptyCaptureStillRequiresExplicitDiskTimingAttachment()
    {
        var attribution = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            Array.Empty<DiskIoEventObservation>(),
            maxOwnersPerDisk: 2);
        var processTiming = DiskIoProcessResponseTimingAnalyzer.Analyze(
            attribution,
            Array.Empty<DiskIoResponseTimingObservation>());
        var capture = DiskIoCaptureResult.Completed(
            new DiskIoCaptureBudget(TimeSpan.FromSeconds(2), 10, 2),
            attribution,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            lostEventCount: 0,
            lostBufferCount: 0,
            providerOverheadDuration: null,
            detail: "empty test");

        Assert.AreEqual(0, processTiming.Count);
        Assert.Throws<InvalidOperationException>(() =>
            capture.WithProcessResponseTimings(processTiming));

        var attached = capture
            .WithResponseTimings(Array.Empty<DiskIoDiskResponseTiming>())
            .WithProcessResponseTimings(processTiming);
        Assert.AreEqual(0, attached.ResponseTimings.Count);
        Assert.AreEqual(0, attached.ProcessResponseTimings.Count);
    }

    private static DiskIoProcessIdentity Owner(int processId, string imageName) =>
        new(processId, StartedAt.AddMinutes(-1), imageName);

    private static DiskIoEventObservation Event(
        int offsetMilliseconds,
        DiskIoOperationKind operation,
        long transferBytes,
        DiskIoProcessIdentity? owner) =>
        new(
            StartedAt.AddMilliseconds(offsetMilliseconds),
            0,
            operation,
            transferBytes,
            owner);

    private static DiskIoResponseTimingObservation Timing(
        int offsetMilliseconds,
        DiskIoOperationKind operation,
        int responseMilliseconds,
        DiskIoProcessIdentity? owner) =>
        new(
            StartedAt.AddMilliseconds(offsetMilliseconds),
            0,
            operation,
            TimeSpan.FromMilliseconds(responseMilliseconds))
        {
            Owner = owner,
        };
}
