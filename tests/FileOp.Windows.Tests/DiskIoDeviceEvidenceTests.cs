using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoDeviceEvidenceTests
{
    [TestMethod]
    public void QueryPreservesDiskOrderAndCallsEachProviderOnce()
    {
        var device = new FakeDeviceContextProvider();
        var nvme = new FakeNvmeHealthProvider();

        var rows = DiskIoDeviceEvidenceCollector.Query(
            [2u, 0u, 7u],
            device,
            nvme);

        CollectionAssert.AreEqual(new[] { 2u, 0u, 7u }, rows.Select(static row => row.PhysicalDiskNumber).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 0, 7 }, device.Calls.ToArray());
        CollectionAssert.AreEqual(new[] { 2, 0, 7 }, nvme.Calls.ToArray());
        Assert.IsTrue(rows.All(static row => row.QueryAttempted));
        Assert.IsTrue(rows.All(static row => row.DeviceContext is not null));
        Assert.IsTrue(rows.All(static row => row.NvmeHealth is not null));
    }

    [TestMethod]
    public void DuplicateDiskNumbersFailBeforeAnyDeviceQuery()
    {
        var device = new FakeDeviceContextProvider();
        var nvme = new FakeNvmeHealthProvider();

        Assert.Throws<ArgumentException>(() =>
            DiskIoDeviceEvidenceCollector.Query(
                [1u, 2u, 1u],
                device,
                nvme));

        Assert.AreEqual(0, device.Calls.Count);
        Assert.AreEqual(0, nvme.Calls.Count);
    }

    [TestMethod]
    public void OutOfRangeDiskNumberRemainsExplicitWithoutQuery()
    {
        var device = new FakeDeviceContextProvider();
        var nvme = new FakeNvmeHealthProvider();

        var rows = DiskIoDeviceEvidenceCollector.Query(
            [uint.MaxValue, 4u],
            device,
            nvme);

        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(uint.MaxValue, rows[0].PhysicalDiskNumber);
        Assert.AreEqual(DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange, rows[0].QueryStatus);
        Assert.IsFalse(rows[0].QueryAttempted);
        Assert.IsNull(rows[0].DeviceContext);
        Assert.IsNull(rows[0].NvmeHealth);
        StringAssert.Contains(rows[0].QueryStatusDetail, "no device metadata query was attempted");
        CollectionAssert.AreEqual(new[] { 4 }, device.Calls.ToArray());
        CollectionAssert.AreEqual(new[] { 4 }, nvme.Calls.ToArray());
    }

    [TestMethod]
    public void QueryBudgetKeepsLaterObservedDisksExplicitWithoutCallingProviders()
    {
        var device = new FakeDeviceContextProvider();
        var nvme = new FakeNvmeHealthProvider();
        var numbers = Enumerable.Range(
                0,
                DiskIoDeviceEvidenceCollector.MaximumQueriedPhysicalDisks + 3)
            .Select(static value => checked((uint)value))
            .ToArray();

        var rows = DiskIoDeviceEvidenceCollector.Query(numbers, device, nvme);

        Assert.AreEqual(numbers.Length, rows.Count);
        Assert.AreEqual(DiskIoDeviceEvidenceCollector.MaximumQueriedPhysicalDisks, device.Calls.Count);
        Assert.AreEqual(DiskIoDeviceEvidenceCollector.MaximumQueriedPhysicalDisks, nvme.Calls.Count);
        Assert.IsTrue(rows.Take(DiskIoDeviceEvidenceCollector.MaximumQueriedPhysicalDisks)
            .All(static row => row.QueryAttempted));
        Assert.IsTrue(rows.Skip(DiskIoDeviceEvidenceCollector.MaximumQueriedPhysicalDisks)
            .All(static row =>
                row.QueryStatus == DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded &&
                row.DeviceContext is null &&
                row.NvmeHealth is null));
    }

    [TestMethod]
    public void MismatchedProviderEvidenceFailsClosed()
    {
        var device = new FakeDeviceContextProvider(offset: 1);
        var nvme = new FakeNvmeHealthProvider();

        Assert.Throws<ArgumentException>(() =>
            DiskIoDeviceEvidenceCollector.Query(
                [3u],
                device,
                nvme));
    }

    [TestMethod]
    public void ManualSkippedRowsCannotCarryProviderEvidence()
    {
        var device = CreateDeviceContextResult(0);
        var nvme = CreateNvmeResult(0);

        Assert.Throws<ArgumentException>(() =>
            new DiskIoPhysicalDiskDeviceEvidence(
                uint.MaxValue,
                DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange,
                device,
                nvme));
        Assert.Throws<ArgumentException>(() =>
            new DiskIoPhysicalDiskDeviceEvidence(
                0,
                DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded,
                device,
                nvme));
    }

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

    private sealed class FakeDeviceContextProvider(int offset = 0)
        : IPhysicalDiskDeviceContextProvider
    {
        public List<int> Calls { get; } = [];

        public PhysicalDiskDeviceContextResult Query(int physicalDiskNumber)
        {
            Calls.Add(physicalDiskNumber);
            return CreateDeviceContextResult(checked(physicalDiskNumber + offset));
        }
    }

    private sealed class FakeNvmeHealthProvider : INvmeHealthEvidenceProvider
    {
        public List<int> Calls { get; } = [];

        public NvmeHealthEvidenceResult Query(int physicalDiskNumber)
        {
            Calls.Add(physicalDiskNumber);
            return CreateNvmeResult(physicalDiskNumber);
        }
    }
}
