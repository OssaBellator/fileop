using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoDeviceFailurePredictionTests
{
    [TestMethod]
    public void AttachmentPreservesOrderAndQueriesOnlyPreviouslyQueriedRows()
    {
        var baseSnapshot = new DiskIoDeviceEvidenceSnapshot(
            [
                CreateQueriedRow(2),
                new DiskIoPhysicalDiskDeviceEvidence(
                    uint.MaxValue,
                    DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange,
                    null,
                    null),
                CreateQueriedRow(0),
                new DiskIoPhysicalDiskDeviceEvidence(
                    7,
                    DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded,
                    null,
                    null),
            ],
            TimeSpan.FromMilliseconds(3));
        var provider = new FakeFailurePredictionProvider();

        var enriched = DiskIoDeviceEvidenceCollector.AttachFailurePrediction(
            baseSnapshot,
            provider);

        CollectionAssert.AreEqual(
            new[] { 2u, uint.MaxValue, 0u, 7u },
            enriched.Select(static row => row.PhysicalDiskNumber).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 0 }, provider.Calls.ToArray());
        Assert.IsNotNull(enriched[0].FailurePrediction);
        Assert.IsNull(enriched[1].FailurePrediction);
        Assert.IsNotNull(enriched[2].FailurePrediction);
        Assert.IsNull(enriched[3].FailurePrediction);
        Assert.AreSame(baseSnapshot[1], enriched[1]);
        Assert.AreSame(baseSnapshot[3], enriched[3]);
        Assert.IsTrue(enriched.QueryElapsed >= baseSnapshot.QueryElapsed);
    }

    [TestMethod]
    public void AttachmentFailsClosedOnMismatchedPredictionDiskIdentity()
    {
        var baseSnapshot = new DiskIoDeviceEvidenceSnapshot(
            [CreateQueriedRow(3)],
            TimeSpan.Zero);
        var provider = new FakeFailurePredictionProvider(offset: 1);

        Assert.Throws<ArgumentException>(() =>
            DiskIoDeviceEvidenceCollector.AttachFailurePrediction(
                baseSnapshot,
                provider));
        CollectionAssert.AreEqual(new[] { 3 }, provider.Calls.ToArray());
    }

    [TestMethod]
    public void AttachmentFailsClosedOnNullPredictionResult()
    {
        var baseSnapshot = new DiskIoDeviceEvidenceSnapshot(
            [CreateQueriedRow(4)],
            TimeSpan.Zero);
        var provider = new NullFailurePredictionProvider();

        Assert.Throws<ArgumentNullException>(() =>
            DiskIoDeviceEvidenceCollector.AttachFailurePrediction(
                baseSnapshot,
                provider));
        Assert.AreEqual(1, provider.Calls);
    }

    [TestMethod]
    public void AttachmentRefusesToQueryAnAlreadyEnrichedSnapshot()
    {
        var prediction = CreateFailurePredictionResult(1);
        var baseSnapshot = new DiskIoDeviceEvidenceSnapshot(
            [new DiskIoPhysicalDiskDeviceEvidence(
                1,
                DiskIoDeviceEvidenceQueryStatus.Queried,
                CreateDeviceContextResult(1),
                CreateNvmeResult(1),
                prediction)],
            TimeSpan.Zero);
        var provider = new FakeFailurePredictionProvider();

        Assert.Throws<ArgumentException>(() =>
            DiskIoDeviceEvidenceCollector.AttachFailurePrediction(
                baseSnapshot,
                provider));
        Assert.AreEqual(0, provider.Calls.Count);
    }

    [TestMethod]
    public void SkippedRowsCannotCarryFailurePredictionEvidence()
    {
        var prediction = CreateFailurePredictionResult(0);

        Assert.Throws<ArgumentException>(() =>
            new DiskIoPhysicalDiskDeviceEvidence(
                0,
                DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded,
                null,
                null,
                prediction));
        Assert.Throws<ArgumentException>(() =>
            new DiskIoPhysicalDiskDeviceEvidence(
                uint.MaxValue,
                DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange,
                null,
                null,
                prediction));
    }

    [TestMethod]
    public void ExistingFourArgumentRowShapeRemainsCompatibleWithoutPrediction()
    {
        var row = CreateQueriedRow(5);

        Assert.IsTrue(row.QueryAttempted);
        Assert.IsNull(row.FailurePrediction);
        StringAssert.Contains(row.QueryStatusDetail, "not attached in this compatibility result");
    }

    private static DiskIoPhysicalDiskDeviceEvidence CreateQueriedRow(int diskNumber) =>
        new(
            checked((uint)diskNumber),
            DiskIoDeviceEvidenceQueryStatus.Queried,
            CreateDeviceContextResult(diskNumber),
            CreateNvmeResult(diskNumber));

    private static PhysicalDiskDeviceContextResult CreateDeviceContextResult(int diskNumber)
    {
        var descriptor = new PhysicalDiskDeviceDescriptor(
            diskNumber,
            17,
            "NVMe",
            "ACME",
            "Disk",
            "1.0",
            null,
            false,
            true);
        var context = new PhysicalDiskDeviceContext(
            descriptor,
            PhysicalDiskBooleanCapability.Available(false, "No seek penalty reported."),
            PhysicalDiskBooleanCapability.Available(true, "TRIM reported enabled."));
        return PhysicalDiskDeviceContextResult.Available(context, "captured");
    }

    private static NvmeHealthEvidenceResult CreateNvmeResult(int diskNumber)
    {
        var evidence = new NvmeHealthEvidence(
            diskNumber,
            new NvmeCriticalWarningEvidence(0),
            300,
            100,
            10,
            5,
            2,
            100,
            0,
            0,
            0,
            0,
            0);
        return NvmeHealthEvidenceResult.Available(evidence, "captured");
    }

    private static PhysicalDiskFailurePredictionResult CreateFailurePredictionResult(
        int diskNumber,
        uint raw = 0) =>
        PhysicalDiskFailurePredictionResult.Available(
            new PhysicalDiskFailurePredictionEvidence(diskNumber, raw),
            TimeSpan.FromMilliseconds(1),
            raw == 0
                ? "No current prediction reported by Windows."
                : "Failure prediction reported by Windows.");

    private sealed class FakeFailurePredictionProvider(int offset = 0)
        : IPhysicalDiskFailurePredictionProvider
    {
        public List<int> Calls { get; } = [];

        public PhysicalDiskFailurePredictionResult Query(int physicalDiskNumber)
        {
            Calls.Add(physicalDiskNumber);
            return CreateFailurePredictionResult(
                checked(physicalDiskNumber + offset),
                raw: physicalDiskNumber == 0 ? 0u : 9u);
        }
    }

    private sealed class NullFailurePredictionProvider
        : IPhysicalDiskFailurePredictionProvider
    {
        public int Calls { get; private set; }

        public PhysicalDiskFailurePredictionResult Query(int physicalDiskNumber)
        {
            Calls++;
            return null!;
        }
    }
}
