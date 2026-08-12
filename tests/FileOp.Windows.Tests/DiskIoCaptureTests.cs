using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoCaptureTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void DefaultBudgetIsShortAndBounded()
    {
        var budget = DiskIoCaptureBudget.Default;

        Assert.AreEqual(TimeSpan.FromSeconds(2), budget.Duration);
        Assert.AreEqual(100_000, budget.MaxObservations);
        Assert.AreEqual(DiskIoAttributionAnalyzer.DefaultMaxOwnersPerDisk, budget.MaxOwnersPerDisk);
        Assert.IsTrue(budget.Duration <= DiskIoCaptureBudget.MaximumDuration);
        Assert.IsTrue(budget.MaxObservations <= DiskIoCaptureBudget.MaximumObservations);
    }

    [TestMethod]
    public void InvalidBudgetValuesFailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskIoCaptureBudget(
            DiskIoCaptureBudget.MinimumDuration - TimeSpan.FromTicks(1),
            1,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskIoCaptureBudget(
            DiskIoCaptureBudget.MaximumDuration + TimeSpan.FromTicks(1),
            1,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskIoCaptureBudget(
            DiskIoCaptureBudget.DefaultDuration,
            0,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskIoCaptureBudget(
            DiskIoCaptureBudget.DefaultDuration,
            DiskIoCaptureBudget.MaximumObservations + 1,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiskIoCaptureBudget(
            DiskIoCaptureBudget.DefaultDuration,
            1,
            0));
    }

    [TestMethod]
    public void CompletedNoLossResultIsNotMarkedIncomplete()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4);
        var report = Report(budget, acceptedEvents: 2, duration: budget.Duration);
        var result = DiskIoCaptureResult.Completed(
            budget,
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            lostEventCount: 0,
            providerOverheadDuration: TimeSpan.FromMilliseconds(12),
            detail: "Bounded system disk-I/O capture completed.");

        Assert.AreEqual(DiskIoCaptureStatus.Completed, result.Status);
        Assert.AreSame(report, result.Report);
        Assert.AreEqual(DiskIoCaptureStopReason.DurationElapsed, result.StopReason);
        Assert.AreEqual(DiskIoCaptureLossState.NoneObserved, result.LossState);
        Assert.AreEqual(0L, result.LostEventCount);
        Assert.IsFalse(result.EvidenceMayBeIncomplete);
    }

    [TestMethod]
    public void ObservationLimitAndEventLossAreExplicitlyIncomplete()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 3, 4);
        var cappedReport = Report(budget, acceptedEvents: 3, duration: TimeSpan.FromMilliseconds(300));
        var durationReport = Report(budget, acceptedEvents: 2, duration: budget.Duration);
        var capped = DiskIoCaptureResult.Completed(
            budget,
            cappedReport,
            DiskIoCaptureStopReason.ObservationLimitReached,
            DiskIoCaptureLossState.NoneObserved,
            lostEventCount: 0,
            providerOverheadDuration: null,
            detail: "Observation cap reached before duration budget.");
        var lost = DiskIoCaptureResult.Completed(
            budget,
            durationReport,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.Observed,
            lostEventCount: 7,
            providerOverheadDuration: null,
            detail: "Provider observed ETW event loss.");
        var unknown = DiskIoCaptureResult.Completed(
            budget,
            durationReport,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.Unknown,
            lostEventCount: null,
            providerOverheadDuration: null,
            detail: "Provider could not determine event loss.");

        Assert.IsTrue(capped.EvidenceMayBeIncomplete);
        Assert.IsTrue(lost.EvidenceMayBeIncomplete);
        Assert.IsTrue(unknown.EvidenceMayBeIncomplete);
    }

    [TestMethod]
    public void CompletedResultMustMatchItsBudget()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 3, 4);

        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            Report(budget, acceptedEvents: 2, duration: TimeSpan.FromMilliseconds(900)),
            DiskIoCaptureStopReason.ObservationLimitReached,
            DiskIoCaptureLossState.NoneObserved,
            0,
            null,
            "Limit reason without a full limit."));

        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            Report(budget, acceptedEvents: 2, duration: TimeSpan.FromMilliseconds(900)),
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            0,
            null,
            "Duration reason without the full requested window."));

        var tooLong = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            StartedAt.AddSeconds(2),
            [],
            budget.MaxOwnersPerDisk);
        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            tooLong,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            0,
            null,
            "Report duration exceeds capture budget."));

        var wrongOwners = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            StartedAt.Add(budget.Duration),
            [],
            maxOwnersPerDisk: budget.MaxOwnersPerDisk + 1);
        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            wrongOwners,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            0,
            null,
            "Report owner limit differs from capture budget."));
    }

    [TestMethod]
    public void MalformedManualReportsFailClosed()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4);
        var negativeDuration = new DiskIoAttributionReport(
            StartedAt,
            StartedAt.AddTicks(-1),
            0,
            budget.MaxOwnersPerDisk,
            []);
        var negativeEvents = new DiskIoAttributionReport(
            StartedAt,
            StartedAt.Add(budget.Duration),
            -1,
            budget.MaxOwnersPerDisk,
            []);

        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            negativeDuration,
            DiskIoCaptureStopReason.ObservationLimitReached,
            DiskIoCaptureLossState.NoneObserved,
            0,
            null,
            "Negative manual duration."));
        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            negativeEvents,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            0,
            null,
            "Negative manual event count."));
    }

    [TestMethod]
    public void LossStateAndCountMustAgree()
    {
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4);
        var report = Report(budget, acceptedEvents: 1, duration: budget.Duration);

        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.NoneObserved,
            null,
            null,
            "No-loss state without explicit zero count."));
        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.Observed,
            0,
            null,
            "Observed loss without positive count."));
        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Completed(
            budget,
            report,
            DiskIoCaptureStopReason.DurationElapsed,
            DiskIoCaptureLossState.Unknown,
            1,
            null,
            "Unknown loss with invented count."));
    }

    [TestMethod]
    public void UnavailableResultsCannotMasqueradeAsSuccessfulEvidence()
    {
        var budget = DiskIoCaptureBudget.Default;
        var permission = DiskIoCaptureResult.Unavailable(
            budget,
            DiskIoCaptureStatus.PermissionRequired,
            providerOverheadDuration: TimeSpan.FromMilliseconds(3),
            detail: "System logger permission is required.");
        var busy = DiskIoCaptureResult.Unavailable(
            budget,
            DiskIoCaptureStatus.SessionUnavailable,
            providerOverheadDuration: null,
            detail: "System logger capacity is unavailable.");

        Assert.IsNull(permission.Report);
        Assert.IsNull(permission.StopReason);
        Assert.AreEqual(DiskIoCaptureLossState.Unknown, permission.LossState);
        Assert.IsNull(permission.LostEventCount);
        Assert.IsFalse(permission.EvidenceMayBeIncomplete);
        Assert.AreEqual(DiskIoCaptureStatus.SessionUnavailable, busy.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskIoCaptureResult.Unavailable(
            budget,
            DiskIoCaptureStatus.Completed,
            null,
            "Completed is not an unavailable state."));
    }

    [TestMethod]
    public void ResultRejectsNegativeOverheadAndBlankDetail()
    {
        var budget = DiskIoCaptureBudget.Default;

        Assert.Throws<ArgumentOutOfRangeException>(() => DiskIoCaptureResult.Unavailable(
            budget,
            DiskIoCaptureStatus.Unsupported,
            TimeSpan.FromTicks(-1),
            "unsupported"));
        Assert.Throws<ArgumentException>(() => DiskIoCaptureResult.Unavailable(
            budget,
            DiskIoCaptureStatus.Unsupported,
            null,
            "   "));
    }

    private static DiskIoAttributionReport Report(
        DiskIoCaptureBudget budget,
        int acceptedEvents,
        TimeSpan duration)
    {
        var observations = Enumerable.Range(0, acceptedEvents)
            .Select(index => new DiskIoEventObservation(
                StartedAt.AddTicks(index + 1),
                0,
                DiskIoOperationKind.Read,
                4_096,
                new DiskIoProcessIdentity(100, StartedAt.AddMinutes(-1), "sample.exe")))
            .ToArray();
        return DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            StartedAt.Add(duration),
            observations,
            budget.MaxOwnersPerDisk);
    }
}
