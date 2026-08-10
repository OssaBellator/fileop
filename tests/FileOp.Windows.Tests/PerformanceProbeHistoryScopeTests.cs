using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class PerformanceProbeHistoryScopeTests
{
    [TestMethod]
    public void HistoryDoesNotMixDifferentRootsWithSameDisplayScope()
    {
        var history = new PerformanceProbeHistory();
        var firstRoot = Snapshot(@"C:\", 10);
        var secondRoot = Snapshot(@"D:\", 90);

        var firstRows = history.AddAndSummarize(firstRoot);
        var secondRows = history.AddAndSummarize(secondRoot);
        var firstAgain = history.SummarizeCurrentSnapshot(firstRoot);

        Assert.AreEqual(1, firstRows.Count);
        Assert.AreEqual(10L, firstRows[0].MedianMicroseconds);
        Assert.AreEqual(1, secondRows.Count);
        Assert.AreEqual(90L, secondRows[0].MedianMicroseconds);
        Assert.AreEqual(1, firstAgain.Count);
        Assert.AreEqual(1, firstAgain[0].SampleCount);
        Assert.AreEqual(10L, firstAgain[0].MedianMicroseconds);
    }

    [TestMethod]
    public void RootIdentityIsCaseInsensitiveForWindowsScopeKeys()
    {
        var history = new PerformanceProbeHistory();
        history.AddAndSummarize(Snapshot(@"C:\Data", 10));
        var rows = history.AddAndSummarize(Snapshot(@"c:\data", 30));

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(2, rows[0].SampleCount);
        Assert.AreEqual(20L, rows[0].MedianMicroseconds);
    }

    private static PerformanceDiagnosticsSnapshot Snapshot(string root, long elapsed) =>
        new(
            DateTimeOffset.UtcNow,
            "Native",
            "Current",
            1,
            root,
            null,
            null,
            [new PerformanceProbeMeasurement(
                "Indexed search probe",
                "native index",
                elapsed,
                "detail")]);
}
