using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class PerformanceProbeHistoryTests
{
    [TestMethod]
    public void HistorySummarizesMedianAndP95AfterFiveSamples()
    {
        var history = new PerformanceProbeHistory();
        PerformanceProbeDistribution? distribution = null;
        foreach (var elapsed in new long[] { 10, 20, 30, 40, 100 })
        {
            distribution = AssertSingle(history.AddAndSummarize(
                Snapshot("Native", "native index", "Indexed search probe", elapsed)));
        }

        Assert.IsNotNull(distribution);
        Assert.AreEqual(PerformanceProbeKind.Search, distribution.Kind);
        Assert.AreEqual(5, distribution.SampleCount);
        Assert.AreEqual(20, distribution.SampleCapacity);
        Assert.AreEqual(10L, distribution.MinimumMicroseconds);
        Assert.AreEqual(30L, distribution.MedianMicroseconds);
        Assert.AreEqual(100L, distribution.P95Microseconds);
        Assert.AreEqual(100L, distribution.MaximumMicroseconds);
    }

    [TestMethod]
    public void HistoryCapsSamplesAndDropsOldest()
    {
        var history = new PerformanceProbeHistory(sampleCapacity: 3);
        PerformanceProbeDistribution? distribution = null;
        foreach (var elapsed in new long[] { 1, 2, 100, 4 })
        {
            distribution = AssertSingle(history.AddAndSummarize(
                Snapshot("Native", "native index", "Indexed search probe", elapsed)));
        }

        Assert.IsNotNull(distribution);
        Assert.AreEqual(3, distribution.SampleCount);
        Assert.AreEqual(3, distribution.SampleCapacity);
        Assert.AreEqual(2L, distribution.MinimumMicroseconds);
        Assert.AreEqual(4L, distribution.MedianMicroseconds);
        Assert.IsNull(distribution.P95Microseconds);
        Assert.AreEqual(100L, distribution.MaximumMicroseconds);
    }

    [TestMethod]
    public void HistorySeparatesScopesAndIgnoresTimerBaseline()
    {
        var history = new PerformanceProbeHistory();
        var native = Snapshot(
            "Native",
            "native index",
            "Indexed search probe",
            20,
            new PerformanceProbeMeasurement(
                "Timer baseline",
                "local process",
                1,
                "detail",
                PerformanceProbeKind.TimerBaseline));
        var fallback = Snapshot(
            "Fallback",
            "profile fallback",
            "Indexed search probe",
            80);

        var nativeRows = history.AddAndSummarize(native);
        var fallbackRows = history.AddAndSummarize(fallback);

        Assert.AreEqual(1, nativeRows.Count);
        Assert.AreEqual("native index", nativeRows[0].Scope);
        Assert.AreEqual(20L, nativeRows[0].MedianMicroseconds);
        Assert.AreEqual(1, fallbackRows.Count);
        Assert.AreEqual("profile fallback", fallbackRows[0].Scope);
        Assert.AreEqual(80L, fallbackRows[0].MedianMicroseconds);
        Assert.IsFalse(fallbackRows.Any(static row => row.Kind == PerformanceProbeKind.TimerBaseline));
    }

    [TestMethod]
    public void LegacyExactProbeNamesResolveKinds()
    {
        var history = new PerformanceProbeHistory();
        var snapshot = new PerformanceDiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            "Native",
            "Current",
            1,
            @"C:\",
            null,
            null,
            [
                new PerformanceProbeMeasurement(
                    "Indexed search probe",
                    "native index",
                    10,
                    "detail"),
                new PerformanceProbeMeasurement(
                    "Storage root probe",
                    "native index",
                    20,
                    "detail"),
                new PerformanceProbeMeasurement(
                    "Timer baseline",
                    "local process",
                    1,
                    "detail"),
            ]);

        var rows = history.AddAndSummarize(snapshot);

        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows.Any(static row => row.Kind == PerformanceProbeKind.Search));
        Assert.IsTrue(rows.Any(static row => row.Kind == PerformanceProbeKind.Storage));
        Assert.IsFalse(rows.Any(static row => row.Kind == PerformanceProbeKind.TimerBaseline));
    }

    [TestMethod]
    public void EvenMedianUsesIntegerMidpointWithoutInventingExtraPrecision()
    {
        var history = new PerformanceProbeHistory();
        history.AddAndSummarize(Snapshot("Native", "native index", "Storage root probe", 10));
        var distribution = AssertSingle(history.AddAndSummarize(
            Snapshot("Native", "native index", "Storage root probe", 21)));

        Assert.AreEqual(15L, distribution.MedianMicroseconds);
        Assert.IsNull(distribution.P95Microseconds);
    }

    private static PerformanceDiagnosticsSnapshot Snapshot(
        string sourceMode,
        string scope,
        string name,
        long elapsed,
        params PerformanceProbeMeasurement[] additional) =>
        new(
            DateTimeOffset.UtcNow,
            sourceMode,
            "Current",
            1,
            @"C:\",
            null,
            null,
            [
                new PerformanceProbeMeasurement(name, scope, elapsed, "detail"),
                .. additional,
            ]);

    private static PerformanceProbeDistribution AssertSingle(
        IReadOnlyList<PerformanceProbeDistribution> rows)
    {
        Assert.AreEqual(1, rows.Count);
        return rows[0];
    }
}
