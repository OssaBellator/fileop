using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class PerformanceProbeHistoryDuplicateTests
{
    [TestMethod]
    public void DuplicateProbeRowsInOneSnapshotCountOnce()
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
                    "first"),
                new PerformanceProbeMeasurement(
                    "Indexed search probe",
                    "native index",
                    20,
                    "duplicate"),
            ]);

        var rows = history.AddAndSummarize(snapshot);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(1, rows[0].SampleCount);
        Assert.AreEqual(10L, rows[0].MedianMicroseconds);
    }
}
