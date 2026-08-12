namespace FileOp.Core.Performance;

public sealed record StartupApplicationDegradationBudget
{
    public static readonly TimeSpan DefaultReadBudget = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinimumReadBudget = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaximumReadBudget = TimeSpan.FromSeconds(10);

    public const int DefaultMaxEvents = 20;
    public const int MaximumEvents = 100;

    public static StartupApplicationDegradationBudget Default { get; } =
        new(DefaultReadBudget, DefaultMaxEvents);

    public StartupApplicationDegradationBudget(
        TimeSpan readBudget,
        int maxEvents)
    {
        if (readBudget < MinimumReadBudget || readBudget > MaximumReadBudget)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readBudget),
                readBudget,
                $"Startup-degradation event read budget must be between {MinimumReadBudget.TotalMilliseconds:N0} ms and {MaximumReadBudget.TotalSeconds:N0} s.");
        }
        if (maxEvents <= 0 || maxEvents > MaximumEvents)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEvents),
                maxEvents,
                $"Startup-degradation event count must be between 1 and {MaximumEvents:N0}.");
        }

        ReadBudget = readBudget;
        MaxEvents = maxEvents;
    }

    public TimeSpan ReadBudget { get; }
    public int MaxEvents { get; }
}

public sealed record StartupApplicationDegradationEvent
{
    public const int MaximumComponentNameLength = 2_048;
    public const int MaximumFriendlyNameLength = 4_096;
    public const int MaximumVersionLength = 512;

    public StartupApplicationDegradationEvent(
        long recordId,
        byte? eventVersion,
        DateTimeOffset recordedAt,
        DateTimeOffset incidentAt,
        string componentName,
        string? friendlyName,
        string? version,
        ulong totalTimeMilliseconds,
        ulong degradationTimeMilliseconds)
    {
        if (recordId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recordId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        if (componentName.Length > MaximumComponentNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(componentName));
        }
        if (friendlyName is { Length: > MaximumFriendlyNameLength })
        {
            throw new ArgumentOutOfRangeException(nameof(friendlyName));
        }
        if (friendlyName is { Length: > 0 } && string.IsNullOrWhiteSpace(friendlyName))
        {
            throw new ArgumentException(
                "A non-empty startup-degradation friendly name cannot contain only whitespace.",
                nameof(friendlyName));
        }
        if (version is { Length: > MaximumVersionLength })
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }
        if (version is { Length: > 0 } && string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException(
                "A non-empty startup-degradation version cannot contain only whitespace.",
                nameof(version));
        }

        RecordId = recordId;
        EventVersion = eventVersion;
        RecordedAt = recordedAt.ToUniversalTime();
        IncidentAt = incidentAt.ToUniversalTime();
        ComponentName = componentName;
        FriendlyName = friendlyName;
        Version = version;
        TotalTimeMilliseconds = totalTimeMilliseconds;
        DegradationTimeMilliseconds = degradationTimeMilliseconds;
    }

    public long RecordId { get; }
    public byte? EventVersion { get; }
    public DateTimeOffset RecordedAt { get; }
    public DateTimeOffset IncidentAt { get; }
    public string ComponentName { get; }
    public string? FriendlyName { get; }
    public string? Version { get; }
    public ulong TotalTimeMilliseconds { get; }
    public ulong DegradationTimeMilliseconds { get; }
}

public enum StartupApplicationDegradationStatus
{
    Completed,
    Unsupported,
    PermissionRequired,
    Cancelled,
    Unavailable,
}

public sealed record StartupApplicationDegradationResult
{
    public StartupApplicationDegradationResult(
        StartupApplicationDegradationBudget budget,
        StartupApplicationDegradationStatus status,
        IReadOnlyList<StartupApplicationDegradationEvent>? events,
        bool moreMatchingEventsAvailable,
        TimeSpan elapsed,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if ((status == StartupApplicationDegradationStatus.Completed) != (events is not null))
        {
            throw new ArgumentException(
                "Completed startup-degradation results require an event collection; unavailable results cannot carry one.",
                nameof(events));
        }
        if (status != StartupApplicationDegradationStatus.Completed && moreMatchingEventsAvailable)
        {
            throw new ArgumentException(
                "Unavailable startup-degradation results cannot claim hidden matching events.",
                nameof(moreMatchingEventsAvailable));
        }

        var snapshot = events?.ToArray();
        if (snapshot is not null)
        {
            if (snapshot.Length > budget.MaxEvents)
            {
                throw new ArgumentException(
                    "Startup-degradation events exceed the requested result-row budget.",
                    nameof(events));
            }
            if (moreMatchingEventsAvailable && snapshot.Length != budget.MaxEvents)
            {
                throw new ArgumentException(
                    "More-events evidence requires a full visible event budget.",
                    nameof(moreMatchingEventsAvailable));
            }

            var ids = new HashSet<long>();
            DateTimeOffset? previousRecordedAt = null;
            foreach (var item in snapshot)
            {
                ArgumentNullException.ThrowIfNull(item);
                if (!ids.Add(item.RecordId))
                {
                    throw new ArgumentException(
                        $"Startup-degradation event record {item.RecordId} appears more than once.",
                        nameof(events));
                }
                if (previousRecordedAt is { } previous && item.RecordedAt > previous)
                {
                    throw new ArgumentException(
                        "Startup-degradation events must remain newest-first.",
                        nameof(events));
                }
                previousRecordedAt = item.RecordedAt;
            }
        }

        Budget = budget;
        Status = status;
        Events = snapshot;
        MoreMatchingEventsAvailable = moreMatchingEventsAvailable;
        Elapsed = elapsed;
        Detail = detail;
    }

    public StartupApplicationDegradationBudget Budget { get; }
    public StartupApplicationDegradationStatus Status { get; }
    public IReadOnlyList<StartupApplicationDegradationEvent>? Events { get; }
    public bool MoreMatchingEventsAvailable { get; }
    public TimeSpan Elapsed { get; }
    public string Detail { get; }

    public static StartupApplicationDegradationResult Completed(
        StartupApplicationDegradationBudget budget,
        IReadOnlyList<StartupApplicationDegradationEvent> events,
        bool moreMatchingEventsAvailable,
        TimeSpan elapsed,
        string detail) =>
        new(
            budget,
            StartupApplicationDegradationStatus.Completed,
            events,
            moreMatchingEventsAvailable,
            elapsed,
            detail);

    public static StartupApplicationDegradationResult Unavailable(
        StartupApplicationDegradationBudget budget,
        StartupApplicationDegradationStatus status,
        TimeSpan elapsed,
        string detail)
    {
        if (status == StartupApplicationDegradationStatus.Completed)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        return new StartupApplicationDegradationResult(
            budget,
            status,
            null,
            false,
            elapsed,
            detail);
    }
}

public interface IStartupApplicationDegradationProvider
{
    StartupApplicationDegradationResult Query(
        StartupApplicationDegradationBudget budget,
        CancellationToken cancellationToken = default);
}
