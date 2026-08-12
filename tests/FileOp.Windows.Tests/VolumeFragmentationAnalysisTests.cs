using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class VolumeFragmentationAnalysisTests
{
    [TestMethod]
    public void ContractRequiresExplicitLocalDriveRoot()
    {
        Assert.AreEqual("C:\\", VolumeFragmentationDriveRoot.RequireCanonical("c:/"));
        Assert.Throws<ArgumentException>(() =>
            VolumeFragmentationDriveRoot.RequireCanonical("C:\\data"));
        Assert.Throws<ArgumentException>(() =>
            VolumeFragmentationDriveRoot.RequireCanonical("\\\\server\\share\\"));
        Assert.Throws<ArgumentException>(() =>
            VolumeFragmentationDriveRoot.RequireCanonical("\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\"));
    }

    [TestMethod]
    public void EvidenceRejectsImpossibleMetrics()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateEvidence(filePercentFragmentation: 101));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateEvidence(totalFiles: 4, totalFragmentedFiles: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateEvidence(freeSpaceBytes: 10, largestFreeSpaceExtentBytes: 11));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateEvidence(averageFragmentsPerFile: double.NaN));
        Assert.Throws<ArgumentException>(() =>
            CreateEvidence(volumeSizeBytes: 100, usedSpaceBytes: 101));
    }

    [TestMethod]
    public void CompletedResultPreservesWindowsRecommendationWithoutFileOpVerdict()
    {
        var budget = VolumeFragmentationAnalysisBudget.Default;
        var evidence = CreateEvidence(windowsDefragRecommended: true);
        var result = VolumeFragmentationAnalysisResult.Completed(
            budget,
            evidence,
            TimeSpan.FromMilliseconds(12),
            "Windows-reported evidence only.");

        Assert.AreEqual(VolumeFragmentationAnalysisStatus.Completed, result.Status);
        Assert.AreEqual((uint?)0, result.ProviderReturnCode);
        Assert.IsNotNull(result.Evidence);
        Assert.IsTrue(result.Evidence.WindowsDefragRecommended);
        Assert.AreEqual("C:\\", result.VolumeRoot);
        Assert.AreEqual(TimeSpan.FromMilliseconds(12), result.Elapsed);
    }

    [TestMethod]
    public async Task ProviderPreservesStructuredMetricsAndExactCallBoundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var api = new FakeApi(new WindowsVolumeFragmentationApiResult(
            0,
            true,
            new WindowsVolumeFragmentationMetrics(
                7,
                1.25,
                100,
                8,
                20,
                4096,
                2048,
                100_000,
                70_000,
                30_000)));
        var provider = new WindowsVolumeFragmentationAnalysisProvider(api);
        var budget = new VolumeFragmentationAnalysisBudget(TimeSpan.FromSeconds(3));

        var result = await provider.AnalyzeAsync("c:/", budget);

        Assert.AreEqual(VolumeFragmentationAnalysisStatus.Completed, result.Status);
        Assert.AreEqual(1, api.Calls);
        Assert.AreEqual("C:\\", api.LastRoot);
        Assert.AreEqual(budget.Timeout, api.LastTimeout);
        Assert.IsNotNull(result.Evidence);
        Assert.IsTrue(result.Evidence.WindowsDefragRecommended);
        Assert.AreEqual((uint)7, result.Evidence.FilePercentFragmentation);
        Assert.AreEqual((ulong)8, result.Evidence.TotalFragmentedFiles);
        Assert.AreEqual(1.25, result.Evidence.AverageFragmentsPerFile);
    }

    [TestMethod]
    public async Task ProviderMapsRawReturnCodesWithoutInventingEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cases = new[]
        {
            (Code: 1u, Status: VolumeFragmentationAnalysisStatus.PermissionRequired),
            (Code: 2u, Status: VolumeFragmentationAnalysisStatus.Unsupported),
            (Code: 6u, Status: VolumeFragmentationAnalysisStatus.Cancelled),
            (Code: 8u, Status: VolumeFragmentationAnalysisStatus.Unavailable),
            (Code: 77u, Status: VolumeFragmentationAnalysisStatus.Unavailable),
        };

        foreach (var item in cases)
        {
            var provider = new WindowsVolumeFragmentationAnalysisProvider(
                new FakeApi(new WindowsVolumeFragmentationApiResult(
                    item.Code,
                    null,
                    null)));
            var result = await provider.AnalyzeAsync(
                "D:\\",
                VolumeFragmentationAnalysisBudget.Default);

            Assert.AreEqual(item.Status, result.Status, $"return code {item.Code}");
            Assert.AreEqual((uint?)item.Code, result.ProviderReturnCode);
            Assert.IsNull(result.Evidence);
        }
    }

    [TestMethod]
    public async Task SuccessWithMalformedMetricsFailsClosed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var provider = new WindowsVolumeFragmentationAnalysisProvider(
            new FakeApi(new WindowsVolumeFragmentationApiResult(
                0,
                false,
                new WindowsVolumeFragmentationMetrics(
                    101,
                    1,
                    10,
                    1,
                    1,
                    10,
                    10,
                    100,
                    50,
                    50))));

        var result = await provider.AnalyzeAsync(
            "C:\\",
            VolumeFragmentationAnalysisBudget.Default);

        Assert.AreEqual(VolumeFragmentationAnalysisStatus.Unavailable, result.Status);
        Assert.AreEqual((uint?)0, result.ProviderReturnCode);
        Assert.IsNull(result.Evidence);
        StringAssert.Contains(result.Detail, "malformed structured evidence");
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesBeforeApiCall()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var api = new FakeApi(new WindowsVolumeFragmentationApiResult(2, null, null));
        var provider = new WindowsVolumeFragmentationAnalysisProvider(api);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.AnalyzeAsync(
                "C:\\",
                VolumeFragmentationAnalysisBudget.Default,
                cancellation.Token));
        Assert.AreEqual(0, api.Calls);
    }

    [TestMethod]
    public void ResultRejectsCompletedWithoutProviderSuccessCode()
    {
        Assert.Throws<ArgumentException>(() =>
            new VolumeFragmentationAnalysisResult(
                "C:\\",
                VolumeFragmentationAnalysisBudget.Default,
                VolumeFragmentationAnalysisStatus.Completed,
                CreateEvidence(),
                2,
                TimeSpan.Zero,
                "invalid"));
    }

    private static VolumeFragmentationEvidence CreateEvidence(
        bool windowsDefragRecommended = false,
        uint filePercentFragmentation = 5,
        double averageFragmentsPerFile = 1.1,
        ulong totalFiles = 100,
        ulong totalFragmentedFiles = 5,
        ulong totalFreeSpaceExtents = 10,
        ulong largestFreeSpaceExtentBytes = 1_000,
        double averageFreeSpacePerExtentBytes = 500,
        ulong volumeSizeBytes = 10_000,
        ulong usedSpaceBytes = 6_000,
        ulong freeSpaceBytes = 4_000) =>
        new(
            "C:\\",
            windowsDefragRecommended,
            filePercentFragmentation,
            averageFragmentsPerFile,
            totalFiles,
            totalFragmentedFiles,
            totalFreeSpaceExtents,
            largestFreeSpaceExtentBytes,
            averageFreeSpacePerExtentBytes,
            volumeSizeBytes,
            usedSpaceBytes,
            freeSpaceBytes);

    private sealed class FakeApi(WindowsVolumeFragmentationApiResult result)
        : IWindowsVolumeFragmentationApi
    {
        public int Calls { get; private set; }
        public string? LastRoot { get; private set; }
        public TimeSpan LastTimeout { get; private set; }

        public ValueTask<WindowsVolumeFragmentationApiResult> AnalyzeAsync(
            string canonicalVolumeRoot,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastRoot = canonicalVolumeRoot;
            LastTimeout = timeout;
            return ValueTask.FromResult(result);
        }
    }
}
