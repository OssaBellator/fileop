using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal sealed record WindowsStartupApplicationDegradationReadResult(
    IReadOnlyList<StartupApplicationDegradationEvent> Events,
    bool MoreMatchingEventsAvailable);

internal interface IWindowsStartupApplicationDegradationEventSource
{
    WindowsStartupApplicationDegradationReadResult Read(
        StartupApplicationDegradationBudget budget,
        CancellationToken cancellationToken);
}

internal sealed class WindowsStartupApplicationDegradationEventSource
    : IWindowsStartupApplicationDegradationEventSource
{
    internal const string ChannelName =
        "Microsoft-Windows-Diagnostics-Performance/Operational";
    internal const string ProviderName =
        "Microsoft-Windows-Diagnostics-Performance";
    internal const int ApplicationDegradationEventId = 101;
    internal const int MaximumEventXmlCharacters = 65_536;

    private static readonly XNamespace EventNamespace =
        "http://schemas.microsoft.com/win/2004/08/events/event";

    public WindowsStartupApplicationDegradationReadResult Read(
        StartupApplicationDegradationBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();

        var started = Stopwatch.GetTimestamp();
        var eventQuery =
            "*[System[Provider[@Name='Microsoft-Windows-Diagnostics-Performance'] and EventID=101]]";
        var query = new EventLogQuery(
            ChannelName,
            PathType.LogName,
            eventQuery)
        {
            ReverseDirection = true,
            TolerateQueryErrors = false,
        };
        using var reader = new EventLogReader(query)
        {
            BatchSize = Math.Min(budget.MaxEvents + 1, 64),
        };
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                reader.CancelReading();
            }
            catch (Exception exception) when (
                exception is ObjectDisposedException or
                EventLogException or
                InvalidOperationException)
            {
                // The read loop or disposal already completed, or the event-log
                // stack rejected a cancellation request that raced completion.
            }
        });

        var events = new List<StartupApplicationDegradationEvent>(budget.MaxEvents);
        var moreMatchingEventsAvailable = false;
        for (var index = 0; index <= budget.MaxEvents; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = GetRemainingReadTimeout(
                budget.ReadBudget,
                Stopwatch.GetElapsedTime(started));

            EventRecord? record;
            try
            {
                record = reader.ReadEvent(remaining);
            }
            catch (EventLogException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (record is null)
            {
                // ReadEvent(TimeSpan) uses the supplied value as the maximum read
                // duration. A null result at/after the total FileOp budget is not
                // safe to reinterpret as a proven end-of-stream.
                if (Stopwatch.GetElapsedTime(started) >= budget.ReadBudget)
                {
                    throw new TimeoutException(
                        "The Diagnostics-Performance event read reached FileOp's total startup-degradation read budget without returning another event.");
                }
                break;
            }

            using (record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index == budget.MaxEvents)
                {
                    moreMatchingEventsAvailable = true;
                    break;
                }

                events.Add(ParseEventXml(record.ToXml()));
            }
        }

        return new WindowsStartupApplicationDegradationReadResult(
            events,
            moreMatchingEventsAvailable);
    }

    internal static TimeSpan GetRemainingReadTimeout(
        TimeSpan budget,
        TimeSpan elapsed)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }

        var remaining = budget - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "The Diagnostics-Performance event query consumed FileOp's startup-degradation read budget.");
        }
        return remaining;
    }

    internal static StartupApplicationDegradationEvent ParseEventXml(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        if (xml.Length > MaximumEventXmlCharacters)
        {
            throw new InvalidDataException(
                $"Diagnostics-Performance event XML exceeded FileOp's {MaximumEventXmlCharacters:N0}-character compatibility bound.");
        }

        var document = XDocument.Parse(xml, LoadOptions.None);
        var eventElement = document.Root;
        if (eventElement?.Name != EventNamespace + "Event")
        {
            throw new InvalidDataException(
                "Diagnostics-Performance compatibility event XML did not contain the expected Event root.");
        }

        var system = RequireElement(eventElement, "System");
        var provider = RequireElement(system, "Provider");
        var providerName = provider.Attribute("Name")?.Value;
        if (!string.Equals(providerName, ProviderName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unexpected Diagnostics-Performance provider '{providerName ?? "<missing>"}'.");
        }

        var eventId = ParseInt32(RequireElement(system, "EventID").Value, "EventID");
        if (eventId != ApplicationDegradationEventId)
        {
            throw new InvalidDataException(
                $"Unexpected Diagnostics-Performance event id {eventId}; expected {ApplicationDegradationEventId}.");
        }

        var channel = RequireElement(system, "Channel").Value;
        if (!string.Equals(channel, ChannelName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unexpected Diagnostics-Performance channel '{channel}'.");
        }

        var recordId = ParseInt64(
            RequireElement(system, "EventRecordID").Value,
            "EventRecordID");
        var recordedAt = ParseTimestamp(
            RequireElement(system, "TimeCreated").Attribute("SystemTime")?.Value,
            "System/TimeCreated@SystemTime");
        var eventVersion = TryParseByte(
            system.Element(EventNamespace + "Version")?.Value,
            "System/Version");

        var eventData = eventElement.Element(EventNamespace + "EventData") ??
            throw new InvalidDataException(
                "Diagnostics-Performance event 101 omitted EventData.");
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in eventData.Elements(EventNamespace + "Data"))
        {
            var name = item.Attribute("Name")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidDataException(
                    "Diagnostics-Performance event 101 contains an unnamed EventData field.");
            }
            if (!data.TryAdd(name, item.Value))
            {
                throw new InvalidDataException(
                    $"Diagnostics-Performance event 101 repeats EventData field '{name}'.");
            }
        }

        var componentName = RequireData(data, "Name").Trim();
        if (componentName.Length == 0)
        {
            throw new InvalidDataException(
                "Diagnostics-Performance event 101 contained an empty EventData/Name field.");
        }
        var friendlyName = NormalizeOptionalData(data, "FriendlyName");
        var version = NormalizeOptionalData(data, "Version");
        var totalTimeMilliseconds = ParseUInt64(
            RequireData(data, "TotalTime"),
            "EventData/TotalTime");
        var degradationTimeMilliseconds = ParseUInt64(
            RequireData(data, "DegradationTime"),
            "EventData/DegradationTime");
        var incidentAt = ParseTimestamp(
            RequireData(data, "StartTime"),
            "EventData/StartTime");

        return new StartupApplicationDegradationEvent(
            recordId,
            eventVersion,
            recordedAt,
            incidentAt,
            componentName,
            friendlyName,
            version,
            totalTimeMilliseconds,
            degradationTimeMilliseconds);
    }

    private static XElement RequireElement(XElement parent, string localName) =>
        parent.Element(EventNamespace + localName) ??
        throw new InvalidDataException(
            $"Diagnostics-Performance event XML omitted System/{localName}.");

    private static string RequireData(
        IReadOnlyDictionary<string, string> data,
        string name) =>
        data.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException(
                $"Diagnostics-Performance event 101 omitted EventData/{name}.");

    private static string? NormalizeOptionalData(
        IReadOnlyDictionary<string, string> data,
        string name)
    {
        if (!data.TryGetValue(name, out var value))
        {
            return null;
        }
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static DateTimeOffset ParseTimestamp(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new InvalidDataException(
                $"Diagnostics-Performance compatibility field {fieldName} was not a valid timestamp.");
        }
        return parsed;
    }

    private static int ParseInt32(string value, string fieldName) =>
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Diagnostics-Performance compatibility field {fieldName} was not a valid Int32.");

    private static long ParseInt64(string value, string fieldName) =>
        long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Diagnostics-Performance compatibility field {fieldName} was not a valid Int64.");

    private static ulong ParseUInt64(string value, string fieldName) =>
        ulong.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Diagnostics-Performance compatibility field {fieldName} was not a valid UInt64.");

    private static byte? TryParseByte(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return byte.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Diagnostics-Performance compatibility field {fieldName} was not a valid byte.");
    }
}

public sealed class WindowsStartupApplicationDegradationProvider
    : IStartupApplicationDegradationProvider
{
    private readonly IWindowsStartupApplicationDegradationEventSource _source;

    public WindowsStartupApplicationDegradationProvider()
        : this(new WindowsStartupApplicationDegradationEventSource())
    {
    }

    internal WindowsStartupApplicationDegradationProvider(
        IWindowsStartupApplicationDegradationEventSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public StartupApplicationDegradationResult Query(
        StartupApplicationDegradationBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return StartupApplicationDegradationResult.Unavailable(
                budget,
                StartupApplicationDegradationStatus.Unsupported,
                TimeSpan.Zero,
                "Windows Diagnostics-Performance startup application degradation compatibility events are available only on Windows.");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var raw = _source.Read(budget, cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started);
            return StartupApplicationDegradationResult.Completed(
                budget,
                raw.Events,
                raw.MoreMatchingEventsAvailable,
                elapsed,
                raw.Events.Count == 0
                    ? "The Diagnostics-Performance compatibility query completed and returned no retained application-startup degradation events. This is not proof that startup was fast or that no startup application had impact."
                    : $"Read {raw.Events.Count:N0} retained Diagnostics-Performance application-startup degradation event(s) newest-first. These are Windows compatibility events, not a FileOp impact score or startup-disable recommendation.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            return Failure(
                budget,
                StartupApplicationDegradationStatus.Cancelled,
                started,
                $"The bounded Diagnostics-Performance event read budget was exhausted: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(
                budget,
                StartupApplicationDegradationStatus.PermissionRequired,
                started,
                exception.Message);
        }
        catch (EventLogNotFoundException exception)
        {
            return Failure(
                budget,
                StartupApplicationDegradationStatus.Unsupported,
                started,
                $"The Diagnostics-Performance Operational channel was not available: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            EventLogException or
            InvalidDataException or
            InvalidOperationException or
            NotSupportedException or
            XmlException)
        {
            return Failure(
                budget,
                StartupApplicationDegradationStatus.Unavailable,
                started,
                exception.Message);
        }
    }

    private static StartupApplicationDegradationResult Failure(
        StartupApplicationDegradationBudget budget,
        StartupApplicationDegradationStatus status,
        long started,
        string detail) =>
        StartupApplicationDegradationResult.Unavailable(
            budget,
            status,
            Stopwatch.GetElapsedTime(started),
            $"Windows Diagnostics-Performance application-startup degradation compatibility evidence was unavailable: {detail}");
}
