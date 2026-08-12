using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StartupApplicationDegradationCompatibilityTests
{
    [TestMethod]
    public void ParserNormalizesBlankOptionalTextAndRejectsBlankComponentName()
    {
        var parsed = WindowsStartupApplicationDegradationEventSource.ParseEventXml(
            CreateEventXml(
                componentName: "example.exe",
                friendlyName: "   ",
                version: ""));

        Assert.AreEqual("example.exe", parsed.ComponentName);
        Assert.IsNull(parsed.FriendlyName);
        Assert.IsNull(parsed.Version);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            WindowsStartupApplicationDegradationEventSource.ParseEventXml(
                CreateEventXml(componentName: "   ")));
    }

    [TestMethod]
    public void ParserRejectsOversizedCompatibilityXml()
    {
        var xml = CreateEventXml(
            componentName: "example.exe",
            friendlyName: new string('x',
                WindowsStartupApplicationDegradationEventSource.MaximumEventXmlCharacters));

        Assert.IsTrue(
            xml.Length > WindowsStartupApplicationDegradationEventSource.MaximumEventXmlCharacters);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            WindowsStartupApplicationDegradationEventSource.ParseEventXml(xml));
    }

    [TestMethod]
    public void EmptyCompatibilityResultDoesNotClaimFastStartupOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new EmptySource();
        var provider = new WindowsStartupApplicationDegradationProvider(source);
        var result = provider.Query(StartupApplicationDegradationBudget.Default);

        Assert.AreEqual(StartupApplicationDegradationStatus.Completed, result.Status);
        Assert.IsNotNull(result.Events);
        Assert.AreEqual(0, result.Events.Count);
        Assert.IsFalse(result.MoreMatchingEventsAvailable);
        StringAssert.Contains(result.Detail, "not proof that startup was fast");
        Assert.AreEqual(1, source.Calls);
    }

    private static string CreateEventXml(
        string componentName,
        string? friendlyName = "Example Application",
        string? version = "1.2.3") =>
        $"""
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <Provider Name="Microsoft-Windows-Diagnostics-Performance" />
            <EventID>101</EventID>
            <Version>1</Version>
            <TimeCreated SystemTime="2026-08-11T08:57:30.0000000+00:00" />
            <EventRecordID>1234</EventRecordID>
            <Channel>Microsoft-Windows-Diagnostics-Performance/Operational</Channel>
          </System>
          <EventData>
            <Data Name="StartTime">2026-08-11T08:54:05.0000000+00:00</Data>
            <Data Name="Name">{componentName}</Data>
            <Data Name="FriendlyName">{friendlyName ?? string.Empty}</Data>
            <Data Name="Version">{version ?? string.Empty}</Data>
            <Data Name="TotalTime">5632</Data>
            <Data Name="DegradationTime">2632</Data>
          </EventData>
        </Event>
        """;

    private sealed class EmptySource : IWindowsStartupApplicationDegradationEventSource
    {
        public int Calls { get; private set; }

        public WindowsStartupApplicationDegradationReadResult Read(
            StartupApplicationDegradationBudget budget,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(budget);
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return new WindowsStartupApplicationDegradationReadResult([], false);
        }
    }
}
