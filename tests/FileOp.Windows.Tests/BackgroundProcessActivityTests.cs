using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class BackgroundProcessActivityTests
{
    private static readonly DateTimeOffset StartAt =
        new(2026, 8, 11, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndAt = StartAt.AddSeconds(1);

    [TestMethod]
    public void StableInstancesProduceCpuDeltasAndDeterministicRows()
    {
        var capture = Identity(10, StartAt.AddMinutes(-5), "fileop.exe");
        var worker = Identity(20, StartAt.AddMinutes(-4), "worker.exe");
        var memory = Identity(30, StartAt.AddMinutes(-3), "memory.exe");
        var budget = new BackgroundProcessActivityBudget(TimeSpan.FromSeconds(1), 100, 2);
        var start = Frame(
            StartAt,
            Snapshot(capture, 100, 100, 90, 5),
            Snapshot(worker, 200, 200, 180, 8),
            Snapshot(memory, 300, 900, 800, 10));
        var end = Frame(
            EndAt,
            Snapshot(capture, 110, 120, 100, 6),
            Snapshot(worker, 240, 220, 190, 9),
            Snapshot(memory, 340, 1_000, 900, 11));

        var report = BackgroundProcessActivityAnalyzer.Analyze(
            budget,
            start,
            end,
            capture.ProcessId,
            TimeSpan.FromMilliseconds(12));

        Assert.AreEqual(3, report.StableMatchedProcessCount);
        Assert.AreEqual(2, report.Rows.Count);
        Assert.AreEqual(memory, report.Rows[0].Identity);
        Assert.AreEqual(worker, report.Rows[1].Identity);
        Assert.AreEqual(TimeSpan.FromMilliseconds(40), report.Rows[0].ProcessorTimeDelta);
        Assert.AreEqual(TimeSpan.FromMilliseconds(40), report.Rows[1].ProcessorTimeDelta);
        Assert.AreEqual(1_000L, report.Rows[0].WorkingSetBytes);
        Assert.AreEqual(1, report.OtherMatchedProcessCount);
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), report.OtherMatchedProcessorTime);
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), report.CaptureProcessProcessorTime);
        Assert.AreEqual(120L, report.CaptureProcessWorkingSetBytes);
        Assert.AreEqual(TimeSpan.FromMilliseconds(90), report.TotalMatchedProcessorTime);
        Assert.IsFalse(report.EvidenceMayBeIncomplete);
    }

    [TestMethod]
    public void PidReuseIsStartedAndExitedInsteadOfMatched()
    {
        var oldInstance = Identity(40, StartAt.AddMinutes(-10), "worker.exe");
        var newInstance = Identity(40, StartAt.AddMilliseconds(100), "worker.exe");
        var stable = Identity(50, StartAt.AddMinutes(-2), "stable.exe");
        var budget = new BackgroundProcessActivityBudget(TimeSpan.FromSeconds(1), 100, 10);
        var start = Frame(
            StartAt,
            Snapshot(oldInstance, 100, 100, 100, 1),
            Snapshot(stable, 100, 100, 100, 1));
        var end = Frame(
            EndAt,
            Snapshot(newInstance, 5, 100, 100, 1),
            Snapshot(stable, 120, 100, 100, 1));

        var report = BackgroundProcessActivityAnalyzer.Analyze(
            budget,
            start,
            end,
            999,
            TimeSpan.Zero);

        Assert.AreEqual(1, report.StableMatchedProcessCount);
        Assert.AreEqual(1, report.StartedDuringSampleCount);
        Assert.AreEqual(1, report.ExitedDuringSampleCount);
        Assert.AreEqual(stable, report.Rows.Single().Identity);
        Assert.IsFalse(report.Rows.Any(row => row.Identity.ProcessId == 40));
    }

    [TestMethod]
    public void InaccessibleAndCapEvidenceMakesReportExplicitlyIncomplete()
    {
        var identity = Identity(10, StartAt.AddMinutes(-1), "worker.exe");
        var budget = new BackgroundProcessActivityBudget(TimeSpan.FromSeconds(1), 2, 1);
        var start = new BackgroundProcessActivityFrame(
            StartAt,
            5,
            1,
            true,
            [Snapshot(identity, 10, 100, 90, 1)]);
        var end = new BackgroundProcessActivityFrame(
            EndAt,
            6,
            2,
            true,
            [Snapshot(identity, 20, 110, 95, 1)]);

        var report = BackgroundProcessActivityAnalyzer.Analyze(
            budget,
            start,
            end,
            99,
            TimeSpan.FromMilliseconds(5));

        Assert.IsTrue(report.EvidenceMayBeIncomplete);
        Assert.IsTrue(report.SnapshotCapReached);
        Assert.AreEqual(1, report.StartInaccessibleProcessCount);
        Assert.AreEqual(2, report.EndInaccessibleProcessCount);
    }

    [TestMethod]
    public void ProcessorTimeRegressionForSameStableInstanceFailsClosed()
    {
        var identity = Identity(10, StartAt.AddMinutes(-1), "worker.exe");
        var budget = new BackgroundProcessActivityBudget(TimeSpan.FromSeconds(1), 10, 10);
        var start = Frame(StartAt, Snapshot(identity, 100, 100, 100, 1));
        var end = Frame(EndAt, Snapshot(identity, 99, 100, 100, 1));

        Assert.ThrowsException<InvalidDataException>(() =>
            BackgroundProcessActivityAnalyzer.Analyze(
                budget,
                start,
                end,
                99,
                TimeSpan.Zero));
    }

    [TestMethod]
    public async Task ProviderUsesExactlyTwoSnapshotsAndOneBoundedDelay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identity = Identity(10, StartAt.AddMinutes(-1), "worker.exe");
        var frames = new Queue<BackgroundProcessActivityFrame>([
            Frame(StartAt, Snapshot(identity, 10, 100, 90, 1)),
            Frame(EndAt, Snapshot(identity, 30, 110, 95, 1)),
        ]);
        var source = new FakeSnapshotSource(frames);
        var delays = new List<TimeSpan>();
        var provider = new WindowsBackgroundProcessActivityProvider(
            source,
            (duration, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(duration);
                return Task.CompletedTask;
            },
            99);
        var budget = new BackgroundProcessActivityBudget(TimeSpan.FromMilliseconds(250), 10, 10);

        var result = await provider.CaptureAsync(budget);

        Assert.AreEqual(BackgroundProcessActivityStatus.Completed, result.Status);
        Assert.IsNotNull(result.Report);
        Assert.AreEqual(2, source.CaptureCalls);
        Assert.AreEqual(1, delays.Count);
        Assert.AreEqual(budget.SamplingDelay, delays[0]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), result.Report.Rows.Single().ProcessorTimeDelta);
    }

    [TestMethod]
    public void NativeSnapshotSourceIncludesCurrentProcessWhenBudgetAllows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new WindowsBackgroundProcessSnapshotSource();
        var frame = source.Capture(BackgroundProcessActivityBudget.MaximumProcessSnapshots);

        Assert.IsTrue(frame.EnumeratedProcessCount > 0);
        var current = frame.Processes.FirstOrDefault(process =>
            process.Identity.ProcessId == Environment.ProcessId);
        if (current is null)
        {
            Assert.IsTrue(frame.SnapshotCapReached);
            return;
        }
        Assert.IsTrue(current.TotalProcessorTime >= TimeSpan.Zero);
        Assert.IsTrue(current.WorkingSetBytes >= 0);
    }

    [TestMethod]
    public void ContractsRejectInvalidBudgetsFramesAndRows()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new BackgroundProcessActivityBudget(TimeSpan.FromMilliseconds(100), 10, 10));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new BackgroundProcessIdentity(0, StartAt, "bad.exe"));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new BackgroundProcessCounterSnapshot(
                Identity(1, StartAt.AddMinutes(-1), "bad.exe"),
                TimeSpan.FromMilliseconds(-1),
                0,
                0,
                0));

        var future = Identity(1, StartAt.AddSeconds(1), "future.exe");
        Assert.ThrowsException<ArgumentException>(() =>
            new BackgroundProcessActivityFrame(
                StartAt,
                1,
                0,
                false,
                [Snapshot(future, 0, 0, 0, 0)]));

        var oldPid = Identity(2, StartAt.AddMinutes(-2), "old.exe");
        var reusedPid = Identity(2, StartAt.AddMinutes(-1), "new.exe");
        Assert.ThrowsException<ArgumentException>(() =>
            new BackgroundProcessActivityFrame(
                StartAt,
                2,
                0,
                false,
                [
                    Snapshot(oldPid, 10, 10, 10, 1),
                    Snapshot(reusedPid, 20, 20, 20, 1),
                ]));
    }

    private static BackgroundProcessIdentity Identity(
        int processId,
        DateTimeOffset startedAt,
        string imageName) =>
        new(processId, startedAt, imageName);

    private static BackgroundProcessCounterSnapshot Snapshot(
        BackgroundProcessIdentity identity,
        int cpuMilliseconds,
        long workingSetBytes,
        long privateMemoryBytes,
        int threadCount) =>
        new(
            identity,
            TimeSpan.FromMilliseconds(cpuMilliseconds),
            workingSetBytes,
            privateMemoryBytes,
            threadCount);

    private static BackgroundProcessActivityFrame Frame(
        DateTimeOffset capturedAt,
        params BackgroundProcessCounterSnapshot[] processes) =>
        new(
            capturedAt,
            processes.Length,
            0,
            false,
            processes);

    private sealed class FakeSnapshotSource(Queue<BackgroundProcessActivityFrame> frames)
        : IWindowsBackgroundProcessSnapshotSource
    {
        public int CaptureCalls { get; private set; }

        public BackgroundProcessActivityFrame Capture(int maxProcessSnapshots)
        {
            CaptureCalls++;
            Assert.IsTrue(maxProcessSnapshots > 0);
            return frames.Dequeue();
        }
    }
}
