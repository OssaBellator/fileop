using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageHistoryPressureEvidenceTests
{
    [TestMethod]
    public void PhysicalGrowthCanBeComparedWithCurrentFreeSpace()
    {
        var older = Snapshot(1, 1_200, 1_000, hour: 1);
        var newer = Snapshot(2, 1_500, 1_200, hour: 2);

        var evidence = StorageHistoryPressureEvidence.Analyze(
            [newer, older],
            volumeTotalBytes: 10_000,
            volumeFreeBytes: 2_000);

        Assert.AreEqual(1_200L, evidence.LatestStorageBytes);
        Assert.IsTrue(evidence.LatestStorageUsesPhysicalAllocation);
        Assert.AreEqual(200L, evidence.StorageDeltaBytes);
        Assert.IsTrue(evidence.DeltaUsesPhysicalAllocation);
        Assert.AreEqual(TimeSpan.FromHours(1), evidence.ObservationInterval);
        Assert.AreEqual(2_000L, evidence.VolumeFreeBytes);
        Assert.AreEqual(20d, evidence.VolumeFreePercent);
        Assert.AreEqual(10d, evidence.FreeSpaceToLastPositivePhysicalGrowthMultiple);
    }

    [TestMethod]
    public void LogicalFallbackNeverClaimsPhysicalHeadroomComparison()
    {
        var older = Snapshot(1, 1_000, allocated: null, hour: 1);
        var newer = Snapshot(2, 1_300, allocated: null, hour: 2);

        var evidence = StorageHistoryPressureEvidence.Analyze(
            [older, newer],
            volumeTotalBytes: 10_000,
            volumeFreeBytes: 2_000);

        Assert.AreEqual(1_300L, evidence.LatestStorageBytes);
        Assert.IsFalse(evidence.LatestStorageUsesPhysicalAllocation);
        Assert.AreEqual(300L, evidence.StorageDeltaBytes);
        Assert.IsFalse(evidence.DeltaUsesPhysicalAllocation);
        Assert.IsNull(evidence.FreeSpaceToLastPositivePhysicalGrowthMultiple);
    }

    [TestMethod]
    public void MixedAllocationEvidenceFallsBackToLogicalDelta()
    {
        var older = Snapshot(1, 1_000, 800, hour: 1);
        var newer = Snapshot(2, 1_500, allocated: null, hour: 2);

        var evidence = StorageHistoryPressureEvidence.Analyze(
            [older, newer],
            volumeTotalBytes: 10_000,
            volumeFreeBytes: 3_000);

        Assert.AreEqual(500L, evidence.StorageDeltaBytes);
        Assert.IsFalse(evidence.DeltaUsesPhysicalAllocation);
        Assert.IsNull(evidence.FreeSpaceToLastPositivePhysicalGrowthMultiple);
    }

    [TestMethod]
    public void NonPositivePhysicalChangeDoesNotProduceGrowthMultiple()
    {
        var older = Snapshot(1, 1_200, 1_000, hour: 1);
        var newer = Snapshot(2, 1_000, 800, hour: 2);

        var evidence = StorageHistoryPressureEvidence.Analyze(
            [older, newer],
            volumeTotalBytes: 10_000,
            volumeFreeBytes: 2_000);

        Assert.AreEqual(-200L, evidence.StorageDeltaBytes);
        Assert.IsTrue(evidence.DeltaUsesPhysicalAllocation);
        Assert.IsNull(evidence.FreeSpaceToLastPositivePhysicalGrowthMultiple);
    }

    [TestMethod]
    public void CapacityRaceIsClampedBeforeHeadroomComparison()
    {
        var older = Snapshot(1, 1_000, 800, hour: 1);
        var newer = Snapshot(2, 1_300, 1_000, hour: 2);

        var evidence = StorageHistoryPressureEvidence.Analyze(
            [older, newer],
            volumeTotalBytes: 1_000,
            volumeFreeBytes: 1_500);

        Assert.AreEqual(1_000L, evidence.VolumeFreeBytes);
        Assert.AreEqual(100d, evidence.VolumeFreePercent);
        Assert.AreEqual(5d, evidence.FreeSpaceToLastPositivePhysicalGrowthMultiple);
    }

    [TestMethod]
    public void OneObservationShowsLatestEvidenceWithoutInventingChange()
    {
        var snapshot = Snapshot(1, 1_300, 1_000, hour: 1);

        var evidence = StorageHistoryPressureEvidence.Analyze(
            [snapshot],
            volumeTotalBytes: 10_000,
            volumeFreeBytes: 2_000);

        Assert.AreEqual(1_000L, evidence.LatestStorageBytes);
        Assert.IsNull(evidence.StorageDeltaBytes);
        Assert.IsNull(evidence.ObservationInterval);
        Assert.IsNull(evidence.FreeSpaceToLastPositivePhysicalGrowthMultiple);
    }

    private static StorageHistorySnapshot Snapshot(
        long id,
        long logical,
        long? allocated,
        int hour) =>
        new(
            id,
            @"C:\",
            new DateTimeOffset(2026, 8, 10, hour, 0, 0, TimeSpan.Zero),
            logical,
            allocated,
            FileCount: 10,
            HardLinkAliasCount: 0,
            TypeCount: 1,
            Categories: []);
}
