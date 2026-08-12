using System.Buffers.Binary;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class PhysicalDiskFailurePredictionTests
{
    [TestMethod]
    public void ContractPreservesRawWindowsPredictionWithoutHealthScoring()
    {
        var zero = new PhysicalDiskFailurePredictionEvidence(3, 0);
        var nonzero = new PhysicalDiskFailurePredictionEvidence(3, 17);

        Assert.AreEqual((uint)0, zero.WindowsPredictFailureValue);
        Assert.IsFalse(zero.FailurePredicted);
        Assert.AreEqual((uint)17, nonzero.WindowsPredictFailureValue);
        Assert.IsTrue(nonzero.FailurePredicted);
    }

    [TestMethod]
    public void ContractRejectsInvalidDiskAndResultShape()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PhysicalDiskFailurePredictionEvidence(-1, 0));

        var evidence = new PhysicalDiskFailurePredictionEvidence(2, 0);
        Assert.Throws<ArgumentException>(() =>
            new PhysicalDiskFailurePredictionResult(
                2,
                PhysicalDiskFailurePredictionStatus.Available,
                null,
                TimeSpan.Zero,
                "invalid"));
        Assert.Throws<ArgumentException>(() =>
            new PhysicalDiskFailurePredictionResult(
                2,
                PhysicalDiskFailurePredictionStatus.Unsupported,
                evidence,
                TimeSpan.Zero,
                "invalid"));
        Assert.Throws<ArgumentException>(() =>
            new PhysicalDiskFailurePredictionResult(
                1,
                PhysicalDiskFailurePredictionStatus.Available,
                evidence,
                TimeSpan.Zero,
                "invalid"));
    }

    [TestMethod]
    public void ParserRequiresCompleteStoragePredictFailureStructure()
    {
        var shortData = new byte[WindowsPhysicalDiskFailurePredictionParser.RequiredBytes - 1];
        Assert.Throws<InvalidDataException>(() =>
            WindowsPhysicalDiskFailurePredictionParser.Parse(shortData));
    }

    [TestMethod]
    public void ParserIgnoresVendorSpecificPayload()
    {
        var first = BuildPayload(5, fill: 0x00);
        var second = BuildPayload(5, fill: 0xFF);

        Assert.AreEqual(
            (uint)5,
            WindowsPhysicalDiskFailurePredictionParser.Parse(first));
        Assert.AreEqual(
            (uint)5,
            WindowsPhysicalDiskFailurePredictionParser.Parse(second));
    }

    [TestMethod]
    public void ProviderUsesExistingZeroAccessOpenBoundaryAndOnePredictionQueryOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var handle = new SafeFileHandle(new IntPtr(123), ownsHandle: false);
        var storage = new FakeStorageApi(
            new WindowsPhysicalDiskOpenResult(handle, 0));
        var prediction = new FakePredictionApi(
            new WindowsPhysicalDiskFailurePredictionQueryResult(
                BuildPayload(9, fill: 0xA5),
                0));
        var provider = new WindowsPhysicalDiskFailurePredictionProvider(
            storage,
            prediction);

        var result = provider.Query(4);

        Assert.AreEqual(1, storage.OpenCalls);
        Assert.AreEqual(4, storage.LastDiskNumber);
        Assert.AreEqual(0, storage.PropertyQueryCalls);
        Assert.AreEqual(1, prediction.Calls);
        Assert.AreEqual(
            PhysicalDiskFailurePredictionStatus.Available,
            result.Status);
        Assert.IsNotNull(result.Evidence);
        Assert.AreEqual((uint)9, result.Evidence.WindowsPredictFailureValue);
        Assert.IsTrue(result.Evidence.FailurePredicted);
        Assert.IsTrue(result.Elapsed >= TimeSpan.Zero);
    }

    [TestMethod]
    public void ProviderMapsInvalidFunctionToUnsupportedOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var handle = new SafeFileHandle(new IntPtr(123), ownsHandle: false);
        var provider = new WindowsPhysicalDiskFailurePredictionProvider(
            new FakeStorageApi(new WindowsPhysicalDiskOpenResult(handle, 0)),
            new FakePredictionApi(
                new WindowsPhysicalDiskFailurePredictionQueryResult(null, 1)));

        var result = provider.Query(0);

        Assert.AreEqual(
            PhysicalDiskFailurePredictionStatus.Unsupported,
            result.Status);
        Assert.IsNull(result.Evidence);
        StringAssert.Contains(result.Detail, "Win32 1");
    }

    [TestMethod]
    public void ProviderKeepsOtherQueryErrorsUnavailableOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var handle = new SafeFileHandle(new IntPtr(123), ownsHandle: false);
        var provider = new WindowsPhysicalDiskFailurePredictionProvider(
            new FakeStorageApi(new WindowsPhysicalDiskOpenResult(handle, 0)),
            new FakePredictionApi(
                new WindowsPhysicalDiskFailurePredictionQueryResult(null, 5)));

        var result = provider.Query(0);

        Assert.AreEqual(
            PhysicalDiskFailurePredictionStatus.Unavailable,
            result.Status);
        Assert.IsNull(result.Evidence);
        StringAssert.Contains(result.Detail, "Win32 5");
    }

    [TestMethod]
    public void ProviderFailsClosedOnShortSuccessfulPayloadOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var handle = new SafeFileHandle(new IntPtr(123), ownsHandle: false);
        var provider = new WindowsPhysicalDiskFailurePredictionProvider(
            new FakeStorageApi(new WindowsPhysicalDiskOpenResult(handle, 0)),
            new FakePredictionApi(
                new WindowsPhysicalDiskFailurePredictionQueryResult(
                    new byte[WindowsPhysicalDiskFailurePredictionParser.RequiredBytes - 1],
                    0)));

        var result = provider.Query(0);

        Assert.AreEqual(
            PhysicalDiskFailurePredictionStatus.Unavailable,
            result.Status);
        Assert.IsNull(result.Evidence);
        StringAssert.Contains(result.Detail, "malformed failure-prediction data");
    }

    [TestMethod]
    public void NativeProviderReturnsConservativeResultForPhysicalDriveZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = new WindowsPhysicalDiskFailurePredictionProvider().Query(0);

        Assert.AreEqual(0, result.PhysicalDiskNumber);
        Assert.IsTrue(result.Elapsed >= TimeSpan.Zero);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Detail));
        if (result.Status == PhysicalDiskFailurePredictionStatus.Available)
        {
            Assert.IsNotNull(result.Evidence);
            Assert.AreEqual(0, result.Evidence.PhysicalDiskNumber);
        }
        else
        {
            Assert.IsNull(result.Evidence);
        }
    }

    private static byte[] BuildPayload(uint predictFailure, byte fill)
    {
        var data = new byte[WindowsPhysicalDiskFailurePredictionParser.RequiredBytes];
        Array.Fill(data, fill);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), predictFailure);
        return data;
    }

    private sealed class FakeStorageApi(WindowsPhysicalDiskOpenResult openResult)
        : IWindowsPhysicalDiskStorageApi
    {
        public int OpenCalls { get; private set; }
        public int LastDiskNumber { get; private set; } = -1;
        public int PropertyQueryCalls { get; private set; }

        public WindowsPhysicalDiskOpenResult Open(int physicalDiskNumber)
        {
            OpenCalls++;
            LastDiskNumber = physicalDiskNumber;
            return openResult;
        }

        public WindowsStoragePropertyQueryResult Query(
            SafeFileHandle handle,
            uint propertyId)
        {
            PropertyQueryCalls++;
            throw new AssertFailedException(
                "Failure prediction must not use IOCTL_STORAGE_QUERY_PROPERTY.");
        }
    }

    private sealed class FakePredictionApi(
        WindowsPhysicalDiskFailurePredictionQueryResult result)
        : IWindowsPhysicalDiskFailurePredictionQueryApi
    {
        public int Calls { get; private set; }

        public WindowsPhysicalDiskFailurePredictionQueryResult Query(
            SafeFileHandle handle)
        {
            Calls++;
            return result;
        }
    }
}
