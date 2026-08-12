using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoAttributionProviderTests
{
    private static readonly DateTimeOffset CaptureStart =
        new(2026, 8, 11, 1, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task DurationCaptureStopsOwnedSessionDrainsAndReturnsNoLossReport()
    {
        var harness = ProviderHarness.Create(
            collection: EmptyCollection(),
            processResult: WindowsDiskIoTraceConsumerPolicy.ErrorWmiInstanceNotFound,
            blockProcessUntilStop: true,
            initialEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            finalEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            delayAsync: static (_, _) => Task.CompletedTask);
        var budget = new DiskIoCaptureBudget(TimeSpan.FromMilliseconds(250), 10, 4);

        var result = await harness.Provider.CaptureAsync(budget);

        Assert.AreEqual(DiskIoCaptureStatus.Completed, result.Status);
        Assert.AreEqual(DiskIoCaptureStopReason.DurationElapsed, result.StopReason);
        Assert.AreEqual(DiskIoCaptureLossState.NoneObserved, result.LossState);
        Assert.AreEqual(0L, result.LostEventCount);
        Assert.AreEqual(0L, result.LostBufferCount);
        Assert.AreEqual(budget.Duration, result.Report!.ObservationDuration);
        Assert.AreEqual(0, result.Report.AcceptedEventCount);
        Assert.AreEqual(0, result.ResponseTimings.Count);
        Assert.AreEqual(1, harness.Control.StopCalls);
        Assert.AreEqual(1, harness.Consumer.CloseCalls);
        Assert.AreEqual(2, harness.Consumer.EvidenceReads);
        Assert.AreEqual(1, harness.Collector.ConfigureCalls);
    }

    [TestMethod]
    public async Task ObservationCapPreservesBufferOnlyLossWithoutInventingEventLoss()
    {
        var observations = new[]
        {
            Observation(CaptureStart.AddMilliseconds(100), 0, 1_000),
            Observation(CaptureStart.AddMilliseconds(200), 0, 2_000),
        };
        var responseTimings = new[]
        {
            ResponseTiming(CaptureStart.AddMilliseconds(100), 0, TimeSpan.FromMilliseconds(1)),
            ResponseTiming(CaptureStart.AddMilliseconds(200), 0, TimeSpan.FromMilliseconds(3)),
        };
        var collection = new WindowsDiskIoCaptureCollectionSnapshot(
            observations,
            new Dictionary<WindowsDiskIoOwnerResolutionStatus, int>
            {
                [WindowsDiskIoOwnerResolutionStatus.ThreadUnavailable] = 2,
            },
            IgnoredEventCount: 7,
            ObservationLimitReached: true)
        {
            ResponseTimings = responseTimings,
        };
        var harness = ProviderHarness.Create(
            collection,
            processResult: WindowsDiskIoTraceConsumerPolicy.ErrorCancelled,
            blockProcessUntilStop: false,
            initialEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            finalEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 3),
            delayAsync: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var budget = new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 2, 4);

        var result = await harness.Provider.CaptureAsync(budget);

        Assert.AreEqual(DiskIoCaptureStopReason.ObservationLimitReached, result.StopReason);
        Assert.AreEqual(DiskIoCaptureLossState.Observed, result.LossState);
        Assert.AreEqual(0L, result.LostEventCount);
        Assert.AreEqual(3L, result.LostBufferCount);
        Assert.AreEqual(2, result.Report!.AcceptedEventCount);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), result.Report.ObservationDuration);
        var timing = AssertSingle(result.ResponseTimings);
        Assert.AreEqual(0u, timing.PhysicalDiskNumber);
        Assert.AreEqual(2, timing.Reads!.SampleCount);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), timing.Reads.Median);
        Assert.IsNull(timing.Reads.P95);
        Assert.IsTrue(result.EvidenceMayBeIncomplete);
        StringAssert.Contains(result.Detail, "2 had unresolved process ownership");
        StringAssert.Contains(result.Detail, "3 lost buffers");
        Assert.AreEqual(1, harness.Control.StopCalls);
        Assert.AreEqual(1, harness.Consumer.CloseCalls);
    }

    [TestMethod]
    public async Task CallerCancellationStopsAndDrainsBeforePropagatingCancellation()
    {
        var harness = ProviderHarness.Create(
            collection: EmptyCollection(),
            processResult: WindowsDiskIoTraceConsumerPolicy.ErrorWmiInstanceNotFound,
            blockProcessUntilStop: true,
            initialEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            finalEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            delayAsync: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var cancellation = new CancellationTokenSource();
        var capture = harness.Provider.CaptureAsync(
            new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 10, 4),
            cancellation.Token).AsTask();
        await harness.Consumer.ProcessStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await capture);

        Assert.AreEqual(1, harness.Control.StopCalls);
        Assert.AreEqual(1, harness.Consumer.CloseCalls);
        Assert.IsTrue(harness.Consumer.ProcessReturned);
    }

    [TestMethod]
    public async Task ClassifiedConsumerOpenFailureStopsOwnedSessionAndReturnsUnavailable()
    {
        var harness = ProviderHarness.Create(
            collection: EmptyCollection(),
            processResult: WindowsDiskIoTraceConsumerPolicy.ErrorSuccess,
            blockProcessUntilStop: false,
            initialEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            finalEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            delayAsync: static (_, _) => Task.CompletedTask);
        harness.Consumer.OpenError = WindowsDiskIoTraceConsumerPolicy.ErrorAccessDenied;

        var result = await harness.Provider.CaptureAsync(DiskIoCaptureBudget.Default);

        Assert.AreEqual(DiskIoCaptureStatus.PermissionRequired, result.Status);
        Assert.IsNull(result.Report);
        Assert.AreEqual(DiskIoCaptureLossState.Unknown, result.LossState);
        Assert.AreEqual(1, harness.Control.StopCalls);
        Assert.AreEqual(0, harness.Consumer.ProcessCalls);
        Assert.AreEqual(0, harness.Consumer.EvidenceReads);
    }

    [TestMethod]
    public async Task EarlyProcessCompletionWithoutObservationCapFailsClosed()
    {
        var harness = ProviderHarness.Create(
            collection: EmptyCollection(),
            processResult: WindowsDiskIoTraceConsumerPolicy.ErrorSuccess,
            blockProcessUntilStop: false,
            initialEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            finalEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            delayAsync: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await harness.Provider.CaptureAsync(DiskIoCaptureBudget.Default));

        StringAssert.Contains(exception.Message, "ended before");
        Assert.AreEqual(1, harness.Control.StopCalls);
        Assert.AreEqual(1, harness.Consumer.CloseCalls);
        Assert.AreEqual(1, harness.Consumer.EvidenceReads);
    }

    [TestMethod]
    public async Task ChangedTraceFrequencyFailsClosedAfterDrain()
    {
        var harness = ProviderHarness.Create(
            collection: EmptyCollection(),
            processResult: WindowsDiskIoTraceConsumerPolicy.ErrorWmiInstanceNotFound,
            blockProcessUntilStop: true,
            initialEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            finalEvidence: new WindowsDiskIoTraceEvidence(1_000_000, 0, 0),
            delayAsync: static (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await harness.Provider.CaptureAsync(
                new DiskIoCaptureBudget(TimeSpan.FromMilliseconds(250), 10, 4)));

        StringAssert.Contains(exception.Message, "timing metadata changed");
        Assert.AreEqual(1, harness.Control.StopCalls);
        Assert.AreEqual(1, harness.Consumer.CloseCalls);
        Assert.IsTrue(harness.Consumer.ProcessReturned);
    }

    [TestMethod]
    public async Task MismatchedResponseTimingCountFailsClosed()
    {
        var observations = new[]
        {
            Observation(CaptureStart.AddMilliseconds(100), 0, 1_000),
            Observation(CaptureStart.AddMilliseconds(200), 0, 2_000),
        };
        var collection = new WindowsDiskIoCaptureCollectionSnapshot(
            observations,
            new Dictionary<WindowsDiskIoOwnerResolutionStatus, int>(),
            IgnoredEventCount: 0,
            ObservationLimitReached: true)
        {
            ResponseTimings =
            [
                ResponseTiming(CaptureStart.AddMilliseconds(100), 0, TimeSpan.FromMilliseconds(1)),
            ],
        };
        var harness = ProviderHarness.Create(
            collection,
            processResult: WindowsDiskIoTraceConsumerPolicy.ErrorCancelled,
            blockProcessUntilStop: false,
            initialEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            finalEvidence: new WindowsDiskIoTraceEvidence(10_000_000, 0, 0),
            delayAsync: static (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await harness.Provider.CaptureAsync(
                new DiskIoCaptureBudget(TimeSpan.FromSeconds(1), 2, 4)));

        StringAssert.Contains(exception.Message, "response-timing evidence count");
    }

    private static WindowsDiskIoCaptureCollectionSnapshot EmptyCollection() =>
        new(
            Array.Empty<DiskIoEventObservation>(),
            new Dictionary<WindowsDiskIoOwnerResolutionStatus, int>(),
            IgnoredEventCount: 0,
            ObservationLimitReached: false);

    private static DiskIoEventObservation Observation(
        DateTimeOffset timestamp,
        uint disk,
        long bytes) =>
        new(timestamp, disk, DiskIoOperationKind.Read, bytes, Owner: null);

    private static DiskIoResponseTimingObservation ResponseTiming(
        DateTimeOffset timestamp,
        uint disk,
        TimeSpan responseTime) =>
        new(timestamp, disk, DiskIoOperationKind.Read, responseTime);

    private static T AssertSingle<T>(IReadOnlyList<T> items)
    {
        Assert.AreEqual(1, items.Count);
        return items[0];
    }

    private sealed class ProviderHarness
    {
        private ProviderHarness(
            WindowsDiskIoAttributionProvider provider,
            FakeTraceControlApi control,
            FakeCaptureConsumerApi consumer,
            FakeCollector collector)
        {
            Provider = provider;
            Control = control;
            Consumer = consumer;
            Collector = collector;
        }

        public WindowsDiskIoAttributionProvider Provider { get; }
        public FakeTraceControlApi Control { get; }
        public FakeCaptureConsumerApi Consumer { get; }
        public FakeCollector Collector { get; }

        public static ProviderHarness Create(
            WindowsDiskIoCaptureCollectionSnapshot collection,
            uint processResult,
            bool blockProcessUntilStop,
            WindowsDiskIoTraceEvidence initialEvidence,
            WindowsDiskIoTraceEvidence finalEvidence,
            Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            var consumer = new FakeCaptureConsumerApi(
                processResult,
                blockProcessUntilStop,
                initialEvidence,
                finalEvidence);
            var control = new FakeTraceControlApi(consumer.ReleaseProcess);
            var collector = new FakeCollector(collection);
            var provider = new WindowsDiskIoAttributionProvider(
                new WindowsDiskIoTraceSessionController(control),
                _ => consumer,
                _ => collector,
                () => CaptureStart,
                delayAsync);
            return new ProviderHarness(provider, control, consumer, collector);
        }
    }

    private sealed class FakeTraceControlApi : IWindowsDiskIoTraceControlApi
    {
        private readonly Action _onStop;

        public FakeTraceControlApi(Action onStop)
        {
            _onStop = onStop;
        }

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public uint StartError { get; set; }
        public uint StopError { get; set; }

        public uint StartTrace(out ulong traceId, string instanceName, IntPtr properties)
        {
            StartCalls++;
            traceId = StartError == WindowsDiskIoSystemSessionPolicy.ErrorSuccess ? 123UL : 0UL;
            return StartError;
        }

        public uint StopTrace(ulong traceId, IntPtr properties)
        {
            StopCalls++;
            _onStop();
            return StopError;
        }
    }

    private sealed class FakeCaptureConsumerApi : IWindowsDiskIoCaptureConsumerApi
    {
        private readonly ManualResetEventSlim _release = new(false);
        private readonly uint _processResult;
        private readonly bool _blockProcessUntilStop;
        private readonly WindowsDiskIoTraceEvidence _initialEvidence;
        private readonly WindowsDiskIoTraceEvidence _finalEvidence;

        public FakeCaptureConsumerApi(
            uint processResult,
            bool blockProcessUntilStop,
            WindowsDiskIoTraceEvidence initialEvidence,
            WindowsDiskIoTraceEvidence finalEvidence)
        {
            _processResult = processResult;
            _blockProcessUntilStop = blockProcessUntilStop;
            _initialEvidence = initialEvidence;
            _finalEvidence = finalEvidence;
        }

        public TaskCompletionSource<bool> ProcessStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint OpenError { get; set; }
        public int ProcessCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int EvidenceReads { get; private set; }
        public bool ProcessReturned { get; private set; }

        public WindowsDiskIoNativeOpenResult OpenRealtime(string sessionName, uint processTraceMode) =>
            OpenError == WindowsDiskIoTraceConsumerPolicy.ErrorSuccess
                ? new WindowsDiskIoNativeOpenResult(456UL, OpenError)
                : new WindowsDiskIoNativeOpenResult(
                    WindowsDiskIoTraceConsumerPolicy.InvalidProcessTraceHandle,
                    OpenError);

        public uint ProcessTrace(ulong processingHandle)
        {
            ProcessCalls++;
            ProcessStarted.TrySetResult(true);
            if (_blockProcessUntilStop)
            {
                _release.Wait();
            }
            ProcessReturned = true;
            return _processResult;
        }

        public uint CloseTrace(ulong processingHandle)
        {
            CloseCalls++;
            _release.Set();
            return WindowsDiskIoTraceConsumerPolicy.ErrorSuccess;
        }

        public WindowsDiskIoTraceEvidence ReadTraceEvidence(ulong processingHandle)
        {
            EvidenceReads++;
            return EvidenceReads == 1 ? _initialEvidence : _finalEvidence;
        }

        public void ReleaseProcess() => _release.Set();
    }

    private sealed class FakeCollector : IWindowsDiskIoCaptureCollector
    {
        private readonly WindowsDiskIoCaptureCollectionSnapshot _snapshot;

        public FakeCollector(WindowsDiskIoCaptureCollectionSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public int ConfigureCalls { get; private set; }
        public bool Disposed { get; private set; }

        public void Configure(
            long performanceCounterFrequency,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd)
        {
            ConfigureCalls++;
            Assert.IsTrue(performanceCounterFrequency > 0);
            Assert.AreEqual(CaptureStart, windowStart);
            Assert.IsTrue(windowEnd > windowStart);
        }

        public bool OnEventRecord(IntPtr eventRecord) => true;

        public bool OnBuffer(IntPtr logfile) => true;

        public WindowsDiskIoCaptureCollectionSnapshot Snapshot() => _snapshot;

        public void Dispose() => Disposed = true;
    }
}
