using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StartupApplicationDegradationTests
{
    private static readonly DateTimeOffset RecordedAt =
        new(2026, 8, 11, 8, 57, 30, TimeSpan.Zero);
    private static readonly DateTimeOffset IncidentAt =
        new(2026, 8, 11, 8, 54, 5, TimeSpan.Zero);

    [TestMethod]
    public void ParserPreservesNamedCompatibilityFields()
    {
        var evidence = WindowsStartupApplicationDegradationEventSource.ParseEventXml(
            CreateEventXml(
                recordId: 1234,
                componentName: "example.exe",
                friendlyName: "Example Application",
                version: "1.2.3",
                totalTimeMilliseconds: 5632,
                degradationTimeMilliseconds: 2632));

        Assert.AreEqual(1234L, evidence.RecordId);
        Assert.AreEqual((byte?)1, evidence.EventVersion);
        Assert.AreEqual(RecordedAt, evidence.RecordedAt);
        Assert.AreEqual(IncidentAt, evidence.IncidentAt);
        Assert.AreEqual("example.exe", evidence.ComponentName);
        Assert.AreEqual("Example Application", evidence.FriendlyName);
        Assert.AreEqual("1.2.3", evidence.Version);
        Assert.AreEqual(5632UL, evidence.TotalTimeMilliseconds);
        Assert.AreEqual(2632UL, evidence.DegradationTimeMilliseconds);
    }

    [TestMethod]
    public void ParserRejectsWrongProviderEventChannelAndDuplicateData()
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsStartupApplicationDegradationEventSource.ParseEventXml(
                CreateEventXml(providerName: "Other-Provider")));
        Assert.Throws<InvalidDataException>(() =>
            WindowsStartupApplicationDegradationEventSource.ParseEventXml(
                CreateEventXml(eventId: 100)));
        Assert.Throws<InvalidDataException>(() =>
            WindowsStartupApplicationDegradationEventSource.ParseEventXml(
                CreateEventXml(channelName: "System")));

        var duplicate = CreateEventXml().Replace(
            "</EventData>",
            "<Data Name=\"TotalTime\">1</Data></EventData>",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            WindowsStartupApplicationDegradationEventSource.ParseEventXml(duplicate));
    }

    [TestMethod]
    public void ParserRejectsMissingRequiredAndMalformedNumericFields()
    {
        var missingName = CreateEventXml().Replace(
            "<Data Name=\"Name\">example.exe</Data>",
            string.Empty,
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            WindowsStartupApplicationDegradationEventSource.ParseEventXml(missingName));

        var badTotal = CreateEventXml().Replace(
            "<Data Name=\"TotalTime\">5632</Data>",
            "<Data Name=\"TotalTime\">not-a-number</Data>",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            WindowsStartupApplicationDegradationEventSource.ParseEventXml(badTotal));
    }

    [TestMethod]
    public void ResultPreservesNewestFirstOrderAndExplicitTruncation()
    {
        var budget = new StartupApplicationDegradationBudget(
            TimeSpan.FromSeconds(1),
            2);
        var events = new[]
        {
            CreateEvidence(2, RecordedAt),
            CreateEvidence(1, RecordedAt.AddMinutes(-1)),
        };

        var result = StartupApplicationDegradationResult.Completed(
            budget,
            events,
            moreMatchingEventsAvailable: true,
            TimeSpan.FromMilliseconds(12),
            "captured");

        Assert.AreEqual(StartupApplicationDegradationStatus.Completed, result.Status);
        Assert.AreEqual(2, result.Events!.Count);
        Assert.IsTrue(result.MoreMatchingEventsAvailable);
        CollectionAssert.AreEqual(
            new long[] { 2, 1 },
            result.Events.Select(static item => item.RecordId).ToArray());
    }

    [TestMethod]
    public void ResultRejectsOutOfOrderDuplicateAndImpossibleTruncationEvidence()
    {
        var budget = new StartupApplicationDegradationBudget(
            TimeSpan.FromSeconds(1),
            2);
        var older = CreateEvidence(1, RecordedAt.AddMinutes(-2));
        var newer = CreateEvidence(2, RecordedAt);

        Assert.Throws<ArgumentException>(() =>
            StartupApplicationDegradationResult.Completed(
                budget,
                [older, newer],
                false,
                TimeSpan.Zero,
                "out of order"));
        Assert.Throws<ArgumentException>(() =>
            StartupApplicationDegradationResult.Completed(
                budget,
                [newer, newer],
                false,
                TimeSpan.Zero,
                "duplicate"));
        Assert.Throws<ArgumentException>(() =>
            StartupApplicationDegradationResult.Completed(
                budget,
                [newer],
                true,
                TimeSpan.Zero,
                "impossible truncation"));
    }

    [TestMethod]
    public void RemainingReadTimeoutUsesOneSharedBudget()
    {
        var remaining = WindowsStartupApplicationDegradationEventSource.GetRemainingReadTimeout(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(750));

        Assert.AreEqual(TimeSpan.FromMilliseconds(1250), remaining);
        Assert.Throws<TimeoutException>(() =>
            WindowsStartupApplicationDegradationEventSource.GetRemainingReadTimeout(
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void CallerCancellationPropagatesBeforePlatformOrSourceAccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new WindowsStartupApplicationDegradationProvider(
            new FakeSource(new WindowsStartupApplicationDegradationReadResult([], false)));

        Assert.Throws<OperationCanceledException>(() =>
            provider.Query(
                StartupApplicationDegradationBudget.Default,
                cancellation.Token));
    }

    [TestMethod]
    public void ProviderPreservesEmptyAndTruncatedCompatibilityEvidenceOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var budget = new StartupApplicationDegradationBudget(
            TimeSpan.FromSeconds(1),
            1);
        var source = new FakeSource(new WindowsStartupApplicationDegradationReadResult(
            [CreateEvidence(7, RecordedAt)],
            true));
        var provider = new WindowsStartupApplicationDegradationProvider(source);

        var result = provider.Query(budget);

        Assert.AreEqual(StartupApplicationDegradationStatus.Completed, result.Status);
        Assert.AreEqual(1, source.Calls);
        Assert.AreEqual(budget, source.LastBudget);
        Assert.AreEqual(1, result.Events!.Count);
        Assert.IsTrue(result.MoreMatchingEventsAvailable);
        Assert.IsTrue(result.Elapsed >= TimeSpan.Zero);
    }

    private static StartupApplicationDegradationEvent CreateEvidence(
        long recordId,
        DateTimeOffset recordedAt) =>
        new(
            recordId,
            1,
            recordedAt,
            recordedAt.AddSeconds(-5),
            $"component-{recordId}.exe",
            $"Component {recordId}",
            "1.0",
            5000,
            1000);

    private static string CreateEventXml(
        long recordId = 1234,
        string providerName = "Microsoft-Windows-Diagnostics-Performance",
        int eventId = 101,
        string channelName = "Microsoft-Windows-Diagnostics-Performance/Operational",
        string componentName = "example.exe",
        string? friendlyName = "Example Application",
        string? version = "1.2.3",
        ulong totalTimeMilliseconds = 5632,
        ulong degradationTimeMilliseconds = 2632) =>
        $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="{providerName}" />
            <EventID>{eventId}</EventID>
            <Version>1</Version>
            <TimeCreated SystemTime="{RecordedAt:O}" />
            <EventRecordID>{recordId}</EventRecordID>
            <Channel>{channelName}</Channel>
          </System>
          <EventData>
            <Data Name="StartTime">{IncidentAt:O}</Data>
            <Data Name="Name">{componentName}</Data>
            <Data Name="FriendlyName">{friendlyName ?? string.Empty}</Data>
            <Data Name="Version">{version ?? string.Empty}</Data>
            <Data Name="TotalTime">{totalTimeMilliseconds}</Data>
            <Data Name="DegradationTime">{degradationTimeMilliseconds}</Data>
          </EventData>
        </Event>
        """;

    private sealed class FakeSource(WindowsStartupApplicationDegradationReadResult result)
        : IWindowsStartupApplicationDegradationEventSource
    {
        public int Calls { get; private set; }
        public StartupApplicationDegradationBudget? LastBudget { get; private set; }

        public WindowsStartupApplicationDegradationReadResult Read(
            StartupApplicationDegradationBudget budget,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastBudget = budget;
            return result;
        }
    }
}
