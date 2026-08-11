using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal interface IWindowsDiskIoCaptureConsumerApi :
    IWindowsDiskIoTraceConsumerApi,
    IWindowsDiskIoTraceEvidenceSource
{
}

public sealed class WindowsDiskIoAttributionProvider : IDiskIoAttributionProvider
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly WindowsDiskIoTraceSessionController _sessionController;
    private readonly Func<IWindowsDiskIoNativeTraceCallbackSink, IWindowsDiskIoCaptureConsumerApi> _consumerApiFactory;
    private readonly Func<int, IWindowsDiskIoCaptureCollector> _collectorFactory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public WindowsDiskIoAttributionProvider()
        : this(
            new WindowsDiskIoTraceSessionController(),
            static sink => new WindowsDiskIoNativeTraceConsumerApi(sink),
            static maxObservations => new WindowsDiskIoCaptureCollector(maxObservations),
            static () => DateTimeOffset.UtcNow,
            static (duration, cancellationToken) => Task.Delay(duration, cancellationToken))
    {
    }

    internal WindowsDiskIoAttributionProvider(
        WindowsDiskIoTraceSessionController sessionController,
        Func<IWindowsDiskIoNativeTraceCallbackSink, IWindowsDiskIoCaptureConsumerApi> consumerApiFactory,
        Func<int, IWindowsDiskIoCaptureCollector> collectorFactory,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _sessionController = sessionController ?? throw new ArgumentNullException(nameof(sessionController));
        _consumerApiFactory = consumerApiFactory ?? throw new ArgumentNullException(nameof(consumerApiFactory));
        _collectorFactory = collectorFactory ?? throw new ArgumentNullException(nameof(collectorFactory));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public async ValueTask<DiskIoCaptureResult> CaptureAsync(
        DiskIoCaptureBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return DiskIoCaptureResult.Unavailable(
                budget,
                DiskIoCaptureStatus.Unsupported,
                providerOverheadDuration: null,
                detail: "Disk-specific ETW attribution is available only on Windows.");
        }

        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CaptureExclusiveAsync(budget, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async ValueTask<DiskIoCaptureResult> CaptureExclusiveAsync(
        DiskIoCaptureBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startResult = _sessionController.Start();
        if (!startResult.Started)
        {
            var failure = startResult.Failure ?? throw new InvalidDataException(
                "DiskIo session start failed without a classified failure.");
            return DiskIoCaptureResult.Unavailable(
                budget,
                failure.Status,
                providerOverheadDuration: null,
                detail: failure.Detail);
        }

        using var session = startResult.Session!;
        using var collector = _collectorFactory(budget.MaxObservations);
        var consumerApi = _consumerApiFactory(collector);
        var consumerOpen = new WindowsDiskIoTraceConsumerLifecycle(consumerApi).Open();
        if (!consumerOpen.Opened)
        {
            var failure = consumerOpen.Failure ?? throw new InvalidDataException(
                "DiskIo consumer open failed without a classified failure.");
            return DiskIoCaptureResult.Unavailable(
                budget,
                failure.Status,
                providerOverheadDuration: null,
                detail: failure.Detail);
        }

        using var consumer = consumerOpen.Consumer!;
        Task<WindowsDiskIoTraceProcessDisposition>? processTask = null;
        var processObserved = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var initialEvidence = consumerApi.ReadTraceEvidence(consumer.ProcessingHandle);
            if (!initialEvidence.HasValidPerformanceCounterFrequency)
            {
                throw new InvalidDataException(
                    $"The DiskIo ETW session reported invalid PerfFreq {initialEvidence.PerformanceCounterFrequency}; FileOp will not substitute another clock.");
            }

            var windowStart = _utcNow().ToUniversalTime();
            var windowEnd = windowStart.Add(budget.Duration);
            collector.Configure(
                initialEvidence.PerformanceCounterFrequency,
                windowStart,
                windowEnd);

            processTask = Task.Factory.StartNew(
                consumer.Process,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            using var durationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var durationTask = _delayAsync(budget.Duration, durationCancellation.Token);
            var completedTask = await Task.WhenAny(processTask, durationTask).ConfigureAwait(false);

            WindowsDiskIoTraceProcessDisposition processDisposition;
            WindowsDiskIoSessionStopDisposition stopDisposition;
            WindowsDiskIoCaptureCollectionSnapshot collection;

            if (completedTask == processTask)
            {
                durationCancellation.Cancel();
                await ObserveExpectedDelayCancellationAsync(
                    durationTask,
                    durationCancellation.Token).ConfigureAwait(false);

                try
                {
                    processDisposition = await processTask.ConfigureAwait(false);
                }
                finally
                {
                    processObserved = true;
                }

                collection = collector.Snapshot();
                stopDisposition = session.Stop();

                cancellationToken.ThrowIfCancellationRequested();
                if (!collection.ObservationLimitReached)
                {
                    throw new InvalidDataException(
                        $"DiskIo ProcessTrace ended before the requested {budget.Duration.TotalMilliseconds:N0} ms window without reaching the observation cap (disposition: {processDisposition}).");
                }
            }
            else
            {
                var callerCancelled = false;
                try
                {
                    await durationTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    callerCancelled = true;
                }

                stopDisposition = session.Stop();
                try
                {
                    processDisposition = await processTask.ConfigureAwait(false);
                }
                finally
                {
                    processObserved = true;
                }

                collection = collector.Snapshot();

                if (callerCancelled || cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            if (!collection.ObservationLimitReached &&
                processDisposition == WindowsDiskIoTraceProcessDisposition.CancelledByConsumer)
            {
                throw new InvalidDataException(
                    "DiskIo consumer cancellation was reported without the collector reaching its observation cap.");
            }

            var finalEvidence = consumerApi.ReadTraceEvidence(consumer.ProcessingHandle);
            if (!finalEvidence.HasValidPerformanceCounterFrequency ||
                finalEvidence.PerformanceCounterFrequency != initialEvidence.PerformanceCounterFrequency)
            {
                throw new InvalidDataException(
                    $"DiskIo trace timing metadata changed or became invalid during capture (initial PerfFreq {initialEvidence.PerformanceCounterFrequency}, final {finalEvidence.PerformanceCounterFrequency}).");
            }

            WindowsDiskIoTimingProvenanceValidator.Validate(
                collection.Observations,
                collection.ResponseTimings);

            var stopReason = collection.ObservationLimitReached
                ? DiskIoCaptureStopReason.ObservationLimitReached
                : DiskIoCaptureStopReason.DurationElapsed;
            var reportEnd = stopReason == DiskIoCaptureStopReason.DurationElapsed
                ? windowEnd
                : collection.Observations.Max(static observation => observation.Timestamp);
            var report = DiskIoAttributionAnalyzer.Analyze(
                windowStart,
                reportEnd,
                collection.Observations,
                budget.MaxOwnersPerDisk);
            var responseTimings = DiskIoResponseTimingAnalyzer.Analyze(
                windowStart,
                reportEnd,
                collection.ResponseTimings);
            var responseTimingSampleCount = responseTimings.Sum(static timing => timing.SampleCount);
            if (responseTimingSampleCount != collection.Observations.Count)
            {
                throw new InvalidDataException(
                    $"DiskIo response-timing evidence count {responseTimingSampleCount:N0} does not match the {collection.Observations.Count:N0} accepted normalized completions.");
            }

            var lossState = finalEvidence.HasReportedLoss
                ? DiskIoCaptureLossState.Observed
                : DiskIoCaptureLossState.NoneObserved;
            var unresolvedCount = collection.UnresolvedOwnerCounts.Values.Aggregate(
                0L,
                static (total, count) => total > long.MaxValue - count
                    ? long.MaxValue
                    : total + count);
            var detail =
                $"Captured {collection.Observations.Count:N0} normalized DiskIo completions with one provenance-bound decoded response-duration sample per completion; " +
                $"{unresolvedCount:N0} had unresolved process ownership; " +
                $"ignored {collection.IgnoredEventCount:N0} non-target/out-of-window records. " +
                $"ETW reported {finalEvidence.EventsLost:N0} lost events and {finalEvidence.BuffersLost:N0} lost buffers. " +
                $"Process disposition: {processDisposition}; session stop: {stopDisposition}.";

            return DiskIoCaptureResult.Completed(
                budget,
                report,
                stopReason,
                lossState,
                lostEventCount: finalEvidence.EventsLost,
                lostBufferCount: finalEvidence.BuffersLost,
                providerOverheadDuration: null,
                detail)
                .WithResponseTimings(responseTimings);
        }
        finally
        {
            Exception? cleanupFailure = null;
            if (processTask is not null && !processObserved)
            {
                if (!session.StopAttempted)
                {
                    try
                    {
                        session.Stop();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }

                if (!processTask.IsCompleted)
                {
                    try
                    {
                        consumer.Close();
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure ??= exception;
                    }
                }

                try
                {
                    await processTask.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }

            if (!session.StopAttempted)
            {
                try
                {
                    session.Stop();
                }
                catch (Exception exception)
                {
                    cleanupFailure ??= exception;
                }
            }

            if (cleanupFailure is not null)
            {
                throw new InvalidOperationException(
                    "DiskIo capture cleanup failed after FileOp attempted to stop its owned session, close its consumer, and drain ProcessTrace.",
                    cleanupFailure);
            }
        }
    }

    private static async Task ObserveExpectedDelayCancellationAsync(
        Task delayTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await delayTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
