using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoAttributionTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddSeconds(5);

    [TestMethod]
    public void AggregatesReadWriteFlushAndObservedByteSharePerPhysicalDisk()
    {
        var owner = Owner(101, StartedAt.AddMinutes(-5), "writer.exe");
        var report = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [
                Event(1, DiskIoOperationKind.Read, 4_096, owner, 1),
                Event(1, DiskIoOperationKind.Write, 8_192, owner, 2),
                Event(1, DiskIoOperationKind.Flush, 0, owner, 3),
            ]);

        var disk = AssertSingle(report.Disks);
        var row = AssertSingle(disk.Owners);
        Assert.AreEqual(1u, disk.PhysicalDiskNumber);
        Assert.AreEqual(4_096L, disk.ReadBytes);
        Assert.AreEqual(8_192L, disk.WriteBytes);
        Assert.AreEqual(1L, disk.ReadOperations);
        Assert.AreEqual(1L, disk.WriteOperations);
        Assert.AreEqual(1L, disk.FlushOperations);
        Assert.AreEqual(12_288L, row.TotalBytes);
        Assert.AreEqual(3L, row.TotalOperations);
        Assert.AreEqual(100d, row.ObservedByteSharePercent);
        Assert.AreEqual(100d, disk.AttributionCoveragePercent);
    }

    [TestMethod]
    public void UnresolvedOwnershipRemainsVisibleAndReducesCoverage()
    {
        var owner = Owner(101, StartedAt.AddMinutes(-5), "reader.exe");
        var report = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [
                Event(0, DiskIoOperationKind.Read, 1_000, owner, 1),
                Event(0, DiskIoOperationKind.Write, 3_000, null, 2),
                Event(0, DiskIoOperationKind.Flush, 0, null, 3),
            ]);

        var disk = AssertSingle(report.Disks);
        Assert.AreEqual(4_000L, disk.TotalBytes);
        Assert.AreEqual(3_000L, disk.UnattributedBytes);
        Assert.AreEqual(1_000L, disk.IdentifiedBytes);
        Assert.AreEqual(25d, disk.AttributionCoveragePercent);
        Assert.AreEqual(1L, disk.UnattributedWriteOperations);
        Assert.AreEqual(1L, disk.UnattributedFlushOperations);
    }

    [TestMethod]
    public void OwnerLimitPreservesHiddenIdentifiedTotals()
    {
        var report = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [
                Event(0, DiskIoOperationKind.Write, 9_000, Owner(1, StartedAt.AddHours(-1), "first.exe"), 1),
                Event(0, DiskIoOperationKind.Read, 7_000, Owner(2, StartedAt.AddHours(-1), "second.exe"), 2),
                Event(0, DiskIoOperationKind.Write, 5_000, Owner(3, StartedAt.AddHours(-1), "third.exe"), 3),
            ],
            maxOwnersPerDisk: 2);

        var disk = AssertSingle(report.Disks);
        Assert.AreEqual(2, disk.Owners.Count);
        Assert.AreEqual(1, disk.OtherIdentifiedOwnerCount);
        Assert.AreEqual(5_000L, disk.OtherIdentifiedBytes);
        Assert.AreEqual(21_000L, disk.TotalBytes);
        Assert.AreEqual(21_000L, disk.IdentifiedBytes);
        Assert.AreEqual(100d, disk.AttributionCoveragePercent);
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            disk.Owners.Select(static row => row.Owner.ProcessId).ToArray());
    }

    [TestMethod]
    public void SamePidDifferentStartTimesRemainSeparateProcessInstances()
    {
        var firstStart = StartedAt.AddHours(-2);
        var secondStart = StartedAt.AddMinutes(-1);
        var report = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [
                Event(0, DiskIoOperationKind.Read, 4_000, Owner(77, firstStart, "worker.exe"), 1),
                Event(0, DiskIoOperationKind.Read, 6_000, Owner(77, secondStart, "worker.exe"), 2),
            ]);

        var disk = AssertSingle(report.Disks);
        Assert.AreEqual(2, disk.Owners.Count);
        Assert.IsTrue(disk.Owners.All(static row => row.Owner.HasStableInstanceIdentity));
        CollectionAssert.AreEquivalent(
            new[] { firstStart, secondStart },
            disk.Owners.Select(static row => row.Owner.StartedAt!.Value).ToArray());
    }

    [TestMethod]
    public void MissingStartTimeIsExplicitlyWeakIdentityButStillBoundedToCaptureWindow()
    {
        var report = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [
                Event(2, DiskIoOperationKind.Read, 2_000, Owner(55, null, "legacy.exe"), 1),
                Event(2, DiskIoOperationKind.Write, 3_000, Owner(55, null, "legacy.exe"), 2),
            ]);

        var row = AssertSingle(AssertSingle(report.Disks).Owners);
        Assert.AreEqual(55, row.Owner.ProcessId);
        Assert.IsFalse(row.Owner.HasStableInstanceIdentity);
        Assert.AreEqual(5_000L, row.TotalBytes);
        Assert.AreEqual(TimeSpan.FromSeconds(5), report.ObservationDuration);
    }

    [TestMethod]
    public void PhysicalDisksAreNeverMixed()
    {
        var owner = Owner(101, StartedAt.AddMinutes(-5), "io.exe");
        var report = DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [
                Event(0, DiskIoOperationKind.Read, 1_000, owner, 1),
                Event(3, DiskIoOperationKind.Write, 8_000, owner, 2),
            ]);

        Assert.AreEqual(2, report.Disks.Count);
        Assert.AreEqual(0u, report.Disks[0].PhysicalDiskNumber);
        Assert.AreEqual(1_000L, report.Disks[0].TotalBytes);
        Assert.AreEqual(3u, report.Disks[1].PhysicalDiskNumber);
        Assert.AreEqual(8_000L, report.Disks[1].TotalBytes);
    }

    [TestMethod]
    public void InvalidProviderEvidenceFailsClosed()
    {
        Assert.Throws<InvalidDataException>(() => DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [Event(0, DiskIoOperationKind.Read, -1, null, 1)]));
        Assert.Throws<InvalidDataException>(() => DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [Event(0, DiskIoOperationKind.Flush, 1, null, 1)]));
        Assert.Throws<InvalidDataException>(() => DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [new DiskIoEventObservation(
                EndedAt.AddTicks(1),
                0,
                DiskIoOperationKind.Read,
                1,
                null)]));
        Assert.Throws<InvalidDataException>(() => DiskIoAttributionAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [Event(0, DiskIoOperationKind.Read, 1, Owner(0, null, null), 1)]));
    }

    private static DiskIoEventObservation Event(
        uint disk,
        DiskIoOperationKind operation,
        long bytes,
        DiskIoProcessIdentity? owner,
        int millisecondOffset) =>
        new(
            StartedAt.AddMilliseconds(millisecondOffset),
            disk,
            operation,
            bytes,
            owner);

    private static DiskIoProcessIdentity Owner(
        int processId,
        DateTimeOffset? startedAt,
        string? imageName) =>
        new(processId, startedAt, imageName);

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.AreEqual(1, items.Count);
        return items[0];
    }
}
