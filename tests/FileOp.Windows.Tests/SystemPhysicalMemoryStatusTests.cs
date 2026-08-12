using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class SystemPhysicalMemoryStatusTests
{
    [TestMethod]
    public void ContractPreservesApproximateWindowsLoadSeparatelyFromByteRatio()
    {
        var status = new SystemPhysicalMemoryStatus(
            totalPhysicalBytes: 1_000,
            availablePhysicalBytes: 900,
            windowsMemoryLoadPercent: 73);

        Assert.AreEqual(1_000UL, status.TotalPhysicalBytes);
        Assert.AreEqual(900UL, status.AvailablePhysicalBytes);
        Assert.AreEqual(100UL, status.UsedPhysicalBytes);
        Assert.AreEqual((uint)73, status.WindowsMemoryLoadPercent);
    }

    [TestMethod]
    public void ContractRejectsImpossiblePhysicalMemoryEvidence()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new SystemPhysicalMemoryStatus(0, 0, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new SystemPhysicalMemoryStatus(100, 101, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new SystemPhysicalMemoryStatus(100, 50, 101));
    }

    [TestMethod]
    public void ResultRequiresEvidenceOnlyWhenAvailable()
    {
        var status = new SystemPhysicalMemoryStatus(100, 25, 75);
        Assert.ThrowsException<ArgumentException>(() =>
            new SystemPhysicalMemoryStatusResult(
                SystemPhysicalMemoryStatusAvailability.Available,
                null,
                TimeSpan.Zero,
                "invalid"));
        Assert.ThrowsException<ArgumentException>(() =>
            new SystemPhysicalMemoryStatusResult(
                SystemPhysicalMemoryStatusAvailability.Unavailable,
                status,
                TimeSpan.Zero,
                "invalid"));
    }

    [TestMethod]
    public void ProviderPreservesNativeSnapshotOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var api = new FakeApi(new WindowsPhysicalMemoryQueryResult(
            new WindowsPhysicalMemorySnapshot(
                32UL * 1024 * 1024 * 1024,
                7UL * 1024 * 1024 * 1024,
                81),
            0));
        var provider = new WindowsSystemPhysicalMemoryStatusProvider(api);

        var result = provider.Query();

        Assert.AreEqual(SystemPhysicalMemoryStatusAvailability.Available, result.Availability);
        Assert.IsNotNull(result.Status);
        Assert.AreEqual(32UL * 1024 * 1024 * 1024, result.Status.TotalPhysicalBytes);
        Assert.AreEqual(7UL * 1024 * 1024 * 1024, result.Status.AvailablePhysicalBytes);
        Assert.AreEqual((uint)81, result.Status.WindowsMemoryLoadPercent);
        Assert.AreEqual(1, api.Calls);
        Assert.IsTrue(result.Elapsed >= TimeSpan.Zero);
    }

    [TestMethod]
    public void ProviderFailsClosedOnMalformedNativeSnapshotOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsSystemPhysicalMemoryStatusProvider(
            new FakeApi(new WindowsPhysicalMemoryQueryResult(
                new WindowsPhysicalMemorySnapshot(100, 101, 50),
                0)));

        var result = provider.Query();

        Assert.AreEqual(SystemPhysicalMemoryStatusAvailability.Unavailable, result.Availability);
        Assert.IsNull(result.Status);
        StringAssert.Contains(result.Detail, "malformed physical-memory evidence");
    }

    [TestMethod]
    public void ProviderPreservesWin32FailureWithoutEvidenceOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsSystemPhysicalMemoryStatusProvider(
            new FakeApi(new WindowsPhysicalMemoryQueryResult(null, 87)));

        var result = provider.Query();

        Assert.AreEqual(SystemPhysicalMemoryStatusAvailability.Unavailable, result.Availability);
        Assert.IsNull(result.Status);
        StringAssert.Contains(result.Detail, "Win32 error 87");
    }

    [TestMethod]
    public void NativeProviderReturnsConservativeEvidenceWhenAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = new WindowsSystemPhysicalMemoryStatusProvider().Query();
        if (result.Availability != SystemPhysicalMemoryStatusAvailability.Available)
        {
            Assert.IsNull(result.Status);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Detail));
            return;
        }

        Assert.IsNotNull(result.Status);
        Assert.IsTrue(result.Status.TotalPhysicalBytes > 0);
        Assert.IsTrue(result.Status.AvailablePhysicalBytes <= result.Status.TotalPhysicalBytes);
        Assert.IsTrue(result.Status.WindowsMemoryLoadPercent <= 100);
    }

    private sealed class FakeApi(WindowsPhysicalMemoryQueryResult result)
        : IWindowsSystemPhysicalMemoryApi
    {
        public int Calls { get; private set; }

        public WindowsPhysicalMemoryQueryResult Query()
        {
            Calls++;
            return result;
        }
    }
}
