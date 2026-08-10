using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal sealed record WindowsDiskIoCaptureCollectionSnapshot(
    IReadOnlyList<DiskIoEventObservation> Observations,
    IReadOnlyDictionary<WindowsDiskIoOwnerResolutionStatus, int> UnresolvedOwnerCounts,
    int IgnoredEventCount,
    bool ObservationLimitReached);

internal interface IWindowsDiskIoCaptureCollector :
    IWindowsDiskIoNativeTraceCallbackSink,
    IDisposable
{
    void Configure(
        long performanceCounterFrequency,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd);

    WindowsDiskIoCaptureCollectionSnapshot Snapshot();
}

internal sealed class WindowsDiskIoCaptureCollector : IWindowsDiskIoCaptureCollector
{
    private readonly object _gate = new();
    private readonly int _maxObservations;
    private readonly WindowsDiskIoIssuingThreadResolver _ownerResolver;
    private readonly List<DiskIoEventObservation> _observations = [];
    private readonly Dictionary<WindowsDiskIoOwnerResolutionStatus, int> _unresolvedOwnerCounts = [];

    private long _performanceCounterFrequency;
    private DateTimeOffset? _windowStart;
    private DateTimeOffset? _windowEnd;
    private int _ignoredEventCount;
    private bool _observationLimitReached;
    private bool _disposed;

    public WindowsDiskIoCaptureCollector(
        int maxObservations,
        WindowsDiskIoIssuingThreadResolver? ownerResolver = null)
    {
        if (maxObservations <= 0 || maxObservations > DiskIoCaptureBudget.MaximumObservations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxObservations),
                maxObservations,
                $"DiskIo collection limit must be between 1 and {DiskIoCaptureBudget.MaximumObservations:N0} observations.");
        }

        _maxObservations = maxObservations;
        _ownerResolver = ownerResolver ?? new WindowsDiskIoIssuingThreadResolver();
    }

    public void Configure(
        long performanceCounterFrequency,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        if (performanceCounterFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(performanceCounterFrequency),
                performanceCounterFrequency,
                "DiskIo capture requires the positive performance-counter frequency reported by the ETW trace.");
        }
        if (windowEnd < windowStart)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowEnd),
                windowEnd,
                "DiskIo capture end cannot precede its start.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_performanceCounterFrequency != 0 || _windowStart.HasValue || _observations.Count != 0)
            {
                throw new InvalidOperationException(
                    "A DiskIo capture collector can be configured only once before event processing starts.");
            }

            _performanceCounterFrequency = performanceCounterFrequency;
            _windowStart = windowStart.ToUniversalTime();
            _windowEnd = windowEnd.ToUniversalTime();
        }
    }

    public bool OnEventRecord(IntPtr eventRecord)
    {
        long performanceCounterFrequency;
        DateTimeOffset windowStart;
        DateTimeOffset windowEnd;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureConfigured();
            if (_observationLimitReached)
            {
                return false;
            }

            performanceCounterFrequency = _performanceCounterFrequency;
            windowStart = _windowStart!.Value;
            windowEnd = _windowEnd!.Value;
        }

        var record = WindowsEtwEventRecordSnapshot.CopyFrom(eventRecord);
        if (!WindowsDiskIoEventRecordBridge.TryDecodeCompletion(
            record,
            performanceCounterFrequency,
            out var decoded) || decoded is null)
        {
            IncrementIgnored();
            return true;
        }

        var observationTimestamp = WindowsDiskIoIssuingThreadResolver.ConvertEventTimestamp(
            decoded.EventTimestamp);
        if (observationTimestamp < windowStart || observationTimestamp > windowEnd)
        {
            IncrementIgnored();
            return true;
        }

        var ownerResolution = _ownerResolver.Resolve(
            decoded.Completion.IssuingThreadId,
            decoded.EventTimestamp);
        var observation = new DiskIoEventObservation(
            observationTimestamp,
            decoded.Completion.PhysicalDiskNumber,
            decoded.Completion.Operation,
            decoded.Completion.TransferBytes,
            ownerResolution.Owner);

        lock (_gate)
        {
            if (_observationLimitReached)
            {
                return false;
            }

            _observations.Add(observation);
            if (!ownerResolution.Resolved)
            {
                _unresolvedOwnerCounts.TryGetValue(ownerResolution.Status, out var current);
                _unresolvedOwnerCounts[ownerResolution.Status] = SaturatingIncrement(current);
            }

            if (_observations.Count >= _maxObservations)
            {
                _observationLimitReached = true;
                return false;
            }

            return true;
        }
    }

    public bool OnBuffer(IntPtr logfile)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureConfigured();
            return !_observationLimitReached;
        }
    }

    public WindowsDiskIoCaptureCollectionSnapshot Snapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureConfigured();
            return new WindowsDiskIoCaptureCollectionSnapshot(
                _observations.ToArray(),
                new Dictionary<WindowsDiskIoOwnerResolutionStatus, int>(_unresolvedOwnerCounts),
                _ignoredEventCount,
                _observationLimitReached);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ownerResolver.Dispose();
    }

    private void IncrementIgnored()
    {
        lock (_gate)
        {
            _ignoredEventCount = SaturatingIncrement(_ignoredEventCount);
        }
    }

    private void EnsureConfigured()
    {
        if (_performanceCounterFrequency <= 0 || !_windowStart.HasValue || !_windowEnd.HasValue)
        {
            throw new InvalidOperationException(
                "DiskIo capture collector must be configured with trace timing and an observation window before callbacks are processed.");
        }
    }

    private static int SaturatingIncrement(int value) =>
        value == int.MaxValue ? int.MaxValue : value + 1;
}
