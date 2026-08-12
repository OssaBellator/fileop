using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoCaptureLossEvidenceTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void BufferOnlyLossIsRepresentableWithoutInventingEventLoss()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4);
        var report = Report(budget);

        var result = DiskIoCaptureResult.Completed(
            budget,
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.Observed,
            lostEventCount: 0,
            lostBufferCount: 3,
            providerOverheadDuration: null,
            detail: "ETW reported buffer loss without a lost-event count.");

        Assert.AreEqual(0L, result.LostEventCount);
        Assert.AreEqual(3L, result.LostBufferCount);
        Assert.IsTrue(result.EvidenceMayBeIncomplete);
    }

    [TestMethod]
    public void EventAndBufferLossRemainSeparateCounts()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4);
        var result = DiskIoCaptureResult.Completed(
            budget,
            Report(budget),
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.Observed,
            lostEventCount: 7,
            lostBufferCount: 2,
            providerOverheadDuration: null,
            detail: "ETW reported event and buffer loss.");

        Assert.AreEqual(7L, result.LostEventCount);
        Assert.AreEqual(2L, result.LostBufferCount);
    }

    [TestMethod]
    public void NoLossRequiresBothCountersToBeZero()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4);
        var report = Report(budget);

        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            lostEventCount: 0,
            lostBufferCount: 1,
            providerOverheadDuration: null,
            detail: "Invalid no-loss state."));
    }

    [TestMethod]
    public void ObservedLossRequiresAtLeastOnePositiveCounter()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4);
        var report = Report(budget);

        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.Observed,
            lostEventCount: 0,
            lostBufferCount: 0,
            providerOverheadDuration: null,
            detail: "Invalid observed-loss state."));
    }

    [TestMethod]
    public void UnknownLossRequiresBothCountersToRemainUnknown()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4);
        var report = Report(budget);

        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.Unknown,
            lostEventCount: null,
            lostBufferCount: 1,
            providerOverheadDuration: null,
            detail: "Invalid unknown-loss state."));
    }

    [TestMethod]
    public void UnavailableResultsKeepBothLossCountersUnknown()
    {
        var result = DiskIoCaptureResult.Unavailable(
            DiskIoCaptureBudget.Default,
            DiskIoCaptureStatus.SessionUnavailable,
            providerOverheadDuration: null,
            detail: "No capture was available.");

        Assert.IsNull(result.LostEventCount);
        Assert.IsNull(result.LostBufferCount);
        Assert.AreEqual(DiskIoCaptureLossState.Unknown, result.LossState);
    }

    private static DiskIoAttributionReport Report(DiskIoCaptureBudget budget) =>
        DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            StartedAt.Add(budget.Duration),
            [],
            budget.MaxOwnersPerDisk);
}
