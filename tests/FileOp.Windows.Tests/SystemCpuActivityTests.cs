using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class SystemCpuActivityTests
{
    [TestMethod]
    public void BudgetRequiresBoundedExplicitDelay()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new SystemCpuActivityBudget(TimeSpan.FromMilliseconds(249)));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new SystemCpuActivityBudget(TimeSpan.FromMilliseconds(3001)));

        var budget = new SystemCpuActivityBudget(TimeSpan.FromMilliseconds(250));
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), budget.SamplingDelay);
    }

    [TestMethod]
    public void AnalyzerSubtractsIdleFromKernelPlusUser()
    {
        var start = Snapshot(
            milliseconds: 0,
            idle: 100,
            kernel: 300,
            user: 200);
        var end = Snapshot(
            milliseconds: 1000,
            idle: 150,
            kernel: 400,
            user: 260);

        var evidence = SystemCpuActivityAnalyzer.Analyze(start, end);

        Assert.AreEqual(TimeSpan.FromTicks(160), evidence.TotalProcessorTimeDelta);
        Assert.AreEqual(TimeSpan.FromTicks(50), evidence.IdleProcessorTimeDelta);
        Assert.AreEqual(TimeSpan.FromTicks(110), evidence.BusyProcessorTimeDelta);
        Assert.AreEqual(68.75d, evidence.BusyPercent);
        Assert.AreEqual(TimeSpan.FromSeconds(1), evidence.ObservationWallDuration);
    }

    [TestMethod]
    public void ZeroTotalIntervalDoesNotInventBusyPercentage()
    {
        var start = Snapshot(0, 100, 200, 300);
        var end = Snapshot(500, 100, 200, 300);

        var evidence = SystemCpuActivityAnalyzer.Analyze(start, end);

        Assert.AreEqual(TimeSpan.Zero, evidence.TotalProcessorTimeDelta);
        Assert.AreEqual(TimeSpan.Zero, evidence.BusyProcessorTimeDelta);
        Assert.IsNull(evidence.BusyPercent);
    }

    [TestMethod]
    public void AnalyzerRejectsCounterRegressionAndImpossibleIdleDelta()
    {
        var start = Snapshot(0, 100, 300, 200);
        var regressed = Snapshot(1000, 99, 300, 200);
        Assert.ThrowsException<InvalidDataException>(() =>
            SystemCpuActivityAnalyzer.Analyze(start, regressed));

        var impossibleIdleDelta = Snapshot(1000, 250, 400, 200);
        Assert.ThrowsException<InvalidDataException>(() =>
            SystemCpuActivityAnalyzer.Analyze(start, impossibleIdleDelta));
    }

    [TestMethod]
    public void SnapshotRejectsIdleGreaterThanKernel()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            Snapshot(0, 201, 200, 0));
    }

    [TestMethod]
    public void ResultRequiresEvidenceOnlyWhenCompleted()
    {
        var budget = SystemCpuActivityBudget.Default;
        var evidence = new SystemCpuActivityEvidence(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            TimeSpan.FromTicks(100),
            TimeSpan.FromTicks(50));

        Assert.ThrowsException<ArgumentException>(() =>
            SystemCpuActivityResult.Unavailable(
                budget,
                SystemCpuActivityStatus.Completed,
                TimeSpan.Zero,
                "invalid"));
        Assert.ThrowsException<ArgumentException>(() =>
            new SystemCpuActivityEvidence(
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                TimeSpan.FromTicks(10),
                TimeSpan.FromTicks(11)));

        var completed = SystemCpuActivityResult.Completed(
            budget,
            evidence,
            TimeSpan.Zero,
            "captured");
        Assert.AreEqual(SystemCpuActivityStatus.Completed, completed.Status);
        Assert.AreSame(evidence, completed.Evidence);
    }

    [TestMethod]
    public async Task ProviderUsesExactlyTwoQueriesAndOneDelayOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var budget = new SystemCpuActivityBudget(TimeSpan.FromMilliseconds(250));
        var source = new FakeSource([
            Success(Snapshot(0, 1_000, 3_000, 2_000)),
            Success(Snapshot(250, 1_100, 3_200, 2_100)),
        ]);
        var delays = new List<TimeSpan>();
        var provider = new WindowsSystemCpuActivityProvider(
            source,
            (duration, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(duration);
                return Task.CompletedTask;
            });

        var result = await provider.CaptureAsync(budget);

        Assert.AreEqual(2, source.Calls);
        CollectionAssert.AreEqual(new[] { budget.SamplingDelay }, delays.ToArray());
        Assert.AreEqual(SystemCpuActivityStatus.Completed, result.Status);
        Assert.IsNotNull(result.Evidence);
        Assert.AreEqual(TimeSpan.FromTicks(300), result.Evidence.TotalProcessorTimeDelta);
        Assert.AreEqual(TimeSpan.FromTicks(100), result.Evidence.IdleProcessorTimeDelta);
        Assert.AreEqual(TimeSpan.FromTicks(200), result.Evidence.BusyProcessorTimeDelta);
        Assert.IsTrue(result.ProviderOverheadDuration >= TimeSpan.Zero);
    }

    [TestMethod]
    public async Task FirstQueryFailureDoesNotStartDelayOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new FakeSource([new WindowsSystemCpuTimeQueryResult(null, 5)]);
        var delays = 0;
        var provider = new WindowsSystemCpuActivityProvider(
            source,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        var result = await provider.CaptureAsync(SystemCpuActivityBudget.Default);

        Assert.AreEqual(1, source.Calls);
        Assert.AreEqual(0, delays);
        Assert.AreEqual(SystemCpuActivityStatus.Unavailable, result.Status);
        Assert.IsNull(result.Evidence);
        StringAssert.Contains(result.Detail, "Win32 error 5");
    }

    [TestMethod]
    public async Task SecondQueryFailurePreservesOneDelayOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new FakeSource([
            Success(Snapshot(0, 100, 300, 200)),
            new WindowsSystemCpuTimeQueryResult(null, 87),
        ]);
        var delays = 0;
        var provider = new WindowsSystemCpuActivityProvider(
            source,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        var result = await provider.CaptureAsync(SystemCpuActivityBudget.Default);

        Assert.AreEqual(2, source.Calls);
        Assert.AreEqual(1, delays);
        Assert.AreEqual(SystemCpuActivityStatus.Unavailable, result.Status);
        Assert.IsNull(result.Evidence);
        StringAssert.Contains(result.Detail, "Win32 error 87");
    }

    [TestMethod]
    public async Task ProviderFailsClosedOnInconsistentIntervalOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new FakeSource([
            Success(Snapshot(0, 100, 300, 200)),
            Success(Snapshot(1000, 99, 300, 200)),
        ]);
        var provider = new WindowsSystemCpuActivityProvider(
            source,
            static (_, _) => Task.CompletedTask);

        var result = await provider.CaptureAsync(SystemCpuActivityBudget.Default);

        Assert.AreEqual(SystemCpuActivityStatus.Unavailable, result.Status);
        Assert.IsNull(result.Evidence);
        StringAssert.Contains(result.Detail, "inconsistent interval evidence");
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new FakeSource([
            Success(Snapshot(0, 100, 300, 200)),
        ]);
        var provider = new WindowsSystemCpuActivityProvider(
            source,
            static (_, cancellationToken) =>
                Task.FromCanceled(cancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await provider.CaptureAsync(
                SystemCpuActivityBudget.Default,
                cancellation.Token));
        Assert.AreEqual(0, source.Calls);
    }

    [TestMethod]
    public async Task NativeProviderReturnsConservativeEvidenceOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var budget = new SystemCpuActivityBudget(
            SystemCpuActivityBudget.MinimumSamplingDelay);
        var result = await new WindowsSystemCpuActivityProvider().CaptureAsync(budget);

        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Detail));
        Assert.IsTrue(result.ProviderOverheadDuration >= TimeSpan.Zero);
        if (result.Status != SystemCpuActivityStatus.Completed)
        {
            Assert.IsNull(result.Evidence);
            return;
        }

        Assert.IsNotNull(result.Evidence);
        Assert.IsTrue(result.Evidence.TotalProcessorTimeDelta >= TimeSpan.Zero);
        Assert.IsTrue(result.Evidence.IdleProcessorTimeDelta >= TimeSpan.Zero);
        Assert.IsTrue(result.Evidence.BusyProcessorTimeDelta >= TimeSpan.Zero);
        if (result.Evidence.BusyPercent is { } busyPercent)
        {
            Assert.IsTrue(busyPercent >= 0d && busyPercent <= 100d);
        }
    }

    private static SystemCpuTimeSnapshot Snapshot(
        int milliseconds,
        ulong idle,
        ulong kernel,
        ulong user) =>
        new(
            DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds),
            idle,
            kernel,
            user);

    private static WindowsSystemCpuTimeQueryResult Success(
        SystemCpuTimeSnapshot snapshot) =>
        new(snapshot, 0);

    private sealed class FakeSource(
        IReadOnlyList<WindowsSystemCpuTimeQueryResult> results)
        : IWindowsSystemCpuTimeSource
    {
        public int Calls { get; private set; }

        public WindowsSystemCpuTimeQueryResult Query()
        {
            if (Calls >= results.Count)
            {
                throw new AssertFailedException("Unexpected extra GetSystemTimes query.");
            }
            return results[Calls++];
        }
    }
}
