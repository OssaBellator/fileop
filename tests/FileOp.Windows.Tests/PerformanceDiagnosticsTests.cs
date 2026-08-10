using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class PerformanceDiagnosticsTests
{
    [TestMethod]
    public void SnapshotReportsCapacityWithoutHealthScoring()
    {
        var snapshot = new PerformanceDiagnosticsSnapshot(
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero),
            "Native",
            "Current",
            42,
            @"C:\",
            1_000,
            250,
            [new PerformanceProbeMeasurement("probe", "scope", 125, "detail")]);

        Assert.AreEqual(750L, snapshot.VolumeUsedBytes);
        Assert.AreEqual(25d, snapshot.VolumeFreePercent);
        Assert.AreEqual(1, snapshot.Probes.Count);
        Assert.AreEqual(125L, snapshot.Probes[0].ElapsedMicroseconds);
    }

    [TestMethod]
    public void SnapshotKeepsUnknownAndOutOfRangeCapacityConservative()
    {
        var unknown = new PerformanceDiagnosticsSnapshot(
            DateTimeOffset.UnixEpoch,
            "Unavailable",
            "No source",
            0,
            null,
            null,
            null,
            []);
        Assert.IsNull(unknown.VolumeUsedBytes);
        Assert.IsNull(unknown.VolumeFreePercent);

        var raced = unknown with
        {
            VolumeTotalBytes = 1_000,
            VolumeFreeBytes = 1_500,
        };
        Assert.AreEqual(0L, raced.VolumeUsedBytes);
        Assert.AreEqual(100d, raced.VolumeFreePercent);
    }
}
