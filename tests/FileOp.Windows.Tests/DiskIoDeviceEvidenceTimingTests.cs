using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoDeviceEvidenceTimingTests
{
    [TestMethod]
    public void SnapshotRejectsNegativeElapsedTime()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DiskIoDeviceEvidenceSnapshot(
                Array.Empty<DiskIoPhysicalDiskDeviceEvidence>(),
                TimeSpan.FromTicks(-1)));
    }

    [TestMethod]
    public void EmptyQueryStillReportsNonNegativeCollectorElapsedTime()
    {
        var snapshot = DiskIoDeviceEvidenceCollector.Query(
            Array.Empty<uint>(),
            new ThrowIfCalledDeviceProvider(),
            new ThrowIfCalledNvmeProvider());

        Assert.AreEqual(0, snapshot.Count);
        Assert.IsTrue(snapshot.QueryElapsed >= TimeSpan.Zero);
    }

    private sealed class ThrowIfCalledDeviceProvider : IPhysicalDiskDeviceContextProvider
    {
        public PhysicalDiskDeviceContextResult Query(int physicalDiskNumber) =>
            throw new AssertFailedException("Empty query must not call the physical-device provider.");
    }

    private sealed class ThrowIfCalledNvmeProvider : INvmeHealthEvidenceProvider
    {
        public NvmeHealthEvidenceResult Query(int physicalDiskNumber) =>
            throw new AssertFailedException("Empty query must not call the NVMe provider.");
    }
}
