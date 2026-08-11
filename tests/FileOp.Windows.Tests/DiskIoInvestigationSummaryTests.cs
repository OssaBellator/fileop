using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoInvestigationSummaryTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 11, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddSeconds(2);

    [TestMethod]
    public void KeepsHighestP95SeparateFromLargestByteDiskAndOwner()
    {
        var ownerA = Owner(10, "latency.exe");
        var ownerB = Owner(20, "bytes.exe");
        var events = new List<DiskIoEventObservation>();
        var timings = new List<DiskIoResponseTimingObservation>();
        for (var index = 0; index < 5; index++)
        {
            events.Add(Event(index, 0, DiskIoOperationKind.Read, 1_000, ownerA));
            timings.Add(Timing(index, 0, DiskIoOperationKind.Read, index == 4 ? 20 : 2, ownerA));

            events.Add(Event(index + 20, 1, DiskIoOperationKind.Write, 4_000, ownerB));
            timings.Add(Timing(index + 20, 1, DiskIoOperationKind.Write, index == 4 ? 8 : 3, ownerB));
        }

        var result = Capture(events, timings);
        var summary = DiskIoInvestigationSummaryAnalyzer.Analyze(result);

        Assert.IsNotNull(summary.HighestObservedP95);
        Assert.AreEqual(0u, summary.HighestObservedP95.PhysicalDiskNumber);
        Assert.AreEqual(DiskIoOperationKind.Read, summary.HighestObservedP95.Operation);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), summary.HighestObservedP95.P95);

        Assert.IsNotNull(summary.LargestObservedByteDisk);
        Assert.AreEqual(1u, summary.LargestObservedByteDisk.PhysicalDiskNumber);
        Assert.AreEqual(20_000L, summary.LargestObservedByteDisk.TotalBytes);

        Assert.IsNotNull(summary.LargestIdentifiedOwner);
        Assert.AreEqual(1u, summary.LargestIdentifiedOwner.PhysicalDiskNumber);
        Assert.AreEqual(ownerB, summary.LargestIdentifiedOwner.Owner);
        Assert.AreEqual(20_000L, summary.LargestIdentifiedOwner.TotalBytes);
    }

    [TestMethod]
    public void P95CueRequiresExistingFiveSampleEligibilityInsteadOfNewThreshold()
    {
        var owner = Owner(10, "worker.exe");
        var events = Enumerable.Range(0, 4)
            .Select(index => Event(index, 0, DiskIoOperationKind.Read, 1_024, owner))
            .ToArray();
        var timings = Enumerable.Range(0, 4)
            .Select(index => Timing(index, 0, DiskIoOperationKind.Read, 500 + index, owner))
            .ToArray();

        var summary = DiskIoInvestigationSummaryAnalyzer.Analyze(Capture(events, timings));

        Assert.IsNull(summary.HighestObservedP95);
        Assert.IsNotNull(summary.LargestObservedByteDisk);
        Assert.IsNotNull(summary.LargestIdentifiedOwner);
    }

    [TestMethod]
    public void EqualP95UsesDiskThenOperationOnlyAsDeterministicTieBreakers()
    {
        var owner = Owner(10, "worker.exe");
        var events = new List<DiskIoEventObservation>();
        var timings = new List<DiskIoResponseTimingObservation>();
        for (var index = 0; index < 5; index++)
        {
            events.Add(Event(index, 1, DiskIoOperationKind.Read, 1_000, owner));
            timings.Add(Timing(index, 1, DiskIoOperationKind.Read, 10, owner));
            events.Add(Event(index + 20, 0, DiskIoOperationKind.Write, 1_000, owner));
            timings.Add(Timing(index + 20, 0, DiskIoOperationKind.Write, 10, owner));
            events.Add(Event(index + 40, 0, DiskIoOperationKind.Read, 1_000, owner));
            timings.Add(Timing(index + 40, 0, DiskIoOperationKind.Read, 10, owner));
        }

        var summary = DiskIoInvestigationSummaryAnalyzer.Analyze(Capture(events, timings));

        Assert.IsNotNull(summary.HighestObservedP95);
        Assert.AreEqual(0u, summary.HighestObservedP95.PhysicalDiskNumber);
        Assert.AreEqual(DiskIoOperationKind.Read, summary.HighestObservedP95.Operation);
    }

    [TestMethod]
    public void IncompleteCaptureFlagIsPreservedWithoutChangingCueSelection()
    {
        var owner = Owner(10, "worker.exe");
        var events = Enumerable.Range(0, 5)
            .Select(index => Event(index, 0, DiskIoOperationKind.Read, 1_024, owner))
            .ToArray();
        var timings = Enumerable.Range(0, 5)
            .Select(index => Timing(index, 0, DiskIoOperationKind.Read, index + 1, owner))
            .ToArray();
        var result = Capture(
            events,
            timings,
            lossState: DiskIoCaptureLossState.Observed,
            lostEventCount: 1,
            lostBufferCount: 0);

        var summary = DiskIoInvestigationSummaryAnalyzer.Analyze(result);

        Assert.IsTrue(summary.EvidenceMayBeIncomplete);
        Assert.IsNotNull(summary.HighestObservedP95);
        Assert.IsNotNull(summary.LargestObservedByteDisk);
    }

    [TestMethod]
    public void EmptyCompletedCaptureProducesNoComparativeCues()
    {
        var result = Capture(
            Array.Empty<DiskIoEventObservation>(),
            Array.Empty<DiskIoResponseTimingObservation>());

        var summary = DiskIoInvestigationSummaryAnalyzer.Analyze(result);

        Assert.AreEqual(0, summary.AcceptedEventCount);
        Assert.IsFalse(summary.HasComparativeCue);
        Assert.IsNull(summary.HighestObservedP95);
        Assert.IsNull(summary.LargestObservedByteDisk);
        Assert.IsNull(summary.LargestIdentifiedOwner);
    }

    [TestMethod]
    public void UnavailableCaptureCannotProduceInvestigationCues()
    {
        var result = DiskIoCaptureResult.Unavailable(
            DiskIoCaptureBudget.Default,
            DiskIoCaptureStatus.Unsupported,
            providerOverheadDuration: null,
            detail: "unsupported");

        Assert.ThrowsException<InvalidOperationException>(() =>
            DiskIoInvestigationSummaryAnalyzer.Analyze(result));
    }

    private static DiskIoCaptureResult Capture(
        IReadOnlyList<DiskIoEventObservation> events,
        IReadOnlyList<DiskIoResponseTimingObservation> timings,
        DiskIoCaptureLossState lossState = DiskIoCaptureLossState.NoneObserved,
        long? lostEventCount = 0,
        long? lostBufferCount = 0)
    {
        var attribution = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            events,
            maxOwnersPerDisk: 4);
        var response = DiskIoResponseTimingAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            timings);
        var capture = DiskIoCaptureResult.Completed(
            new DiskIoCaptureBudget(TimeSpan.FromSeconds(2), 100, 4),
            attribution,
            DiskIoCaptureStopReason.DurationElapsed,
            lossState,
            lostEventCount,
            lostBufferCount,
            providerOverheadDuration: null,
            detail: "test capture");
        return capture.WithResponseTimings(response);
    }

    private static DiskIoProcessIdentity Owner(int processId, string imageName) =>
        new(processId, StartedAt.AddMinutes(-1), imageName);

    private static DiskIoEventObservation Event(
        int offsetMilliseconds,
        uint disk,
        DiskIoOperationKind operation,
        long bytes,
        DiskIoProcessIdentity? owner) =>
        new(
            StartedAt.AddMilliseconds(offsetMilliseconds),
            disk,
            operation,
            bytes,
            owner);

    private static DiskIoResponseTimingObservation Timing(
        int offsetMilliseconds,
        uint disk,
        DiskIoOperationKind operation,
        int responseMilliseconds,
        DiskIoProcessIdentity? owner) =>
        new(
            StartedAt.AddMilliseconds(offsetMilliseconds),
            disk,
            operation,
            TimeSpan.FromMilliseconds(responseMilliseconds))
        {
            Owner = owner,
        };
}
