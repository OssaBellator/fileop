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
        Assert.IsNull(snapshot.FileOpResources);
        Assert.IsNull(snapshot.FileOpResourcesStatus);
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

    [TestMethod]
    public void FileOpResourceSnapshotKeepsMeasuredUnitsSeparate()
    {
        var startedAt = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var resources = new FileOpProcessResourceSnapshot(
            startedAt,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromSeconds(12.5),
            WorkingSetBytes: 120_000_000,
            PeakWorkingSetBytes: 180_000_000,
            PrivateMemoryBytes: 150_000_000,
            ManagedMemoryBytes: 24_000_000,
            ThreadCount: 18);
        var snapshot = new PerformanceDiagnosticsSnapshot(
            startedAt.AddMinutes(15),
            "Native",
            "Current",
            42,
            @"C:\",
            1_000,
            250,
            [],
            FileOpResources: resources);

        Assert.IsTrue(resources.HasValidNonNegativeEvidence);
        Assert.AreSame(resources, snapshot.FileOpResources);
        Assert.AreEqual(TimeSpan.FromSeconds(12.5), snapshot.FileOpResources!.TotalProcessorTime);
        Assert.AreEqual(120_000_000L, snapshot.FileOpResources.WorkingSetBytes);
        Assert.AreEqual(150_000_000L, snapshot.FileOpResources.PrivateMemoryBytes);
        Assert.AreEqual(24_000_000L, snapshot.FileOpResources.ManagedMemoryBytes);
        Assert.AreEqual(18, snapshot.FileOpResources.ThreadCount);
    }

    [TestMethod]
    public void FileOpResourceSnapshotRejectsNegativeEvidenceWithoutInventingPressure()
    {
        var invalid = new FileOpProcessResourceSnapshot(
            DateTimeOffset.UnixEpoch,
            TimeSpan.Zero,
            TimeSpan.Zero,
            WorkingSetBytes: -1,
            PeakWorkingSetBytes: 0,
            PrivateMemoryBytes: 0,
            ManagedMemoryBytes: 0,
            ThreadCount: 0);

        Assert.IsFalse(invalid.HasValidNonNegativeEvidence);
    }
}
