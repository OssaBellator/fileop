using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoAttributionValidationTests
{
    [TestMethod]
    public void ProcessStartAfterAttributedEventFailsClosed()
    {
        var startedAt = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);
        var eventAt = startedAt.AddSeconds(1);
        var owner = new DiskIoProcessIdentity(
            123,
            eventAt.AddTicks(1),
            "late.exe");

        Assert.Throws<InvalidDataException>(() => DiskIoAttributionAnalyzer.Analyze(
            startedAt,
            startedAt.AddSeconds(5),
            [new DiskIoEventObservation(
                eventAt,
                0,
                DiskIoOperationKind.Read,
                4_096,
                owner)]));
    }

    [TestMethod]
    public void InvalidCaptureWindowAndOwnerLimitsFailClosed()
    {
        var startedAt = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(() => DiskIoAttributionAnalyzer.Analyze(
            startedAt,
            startedAt.AddTicks(-1),
            []));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskIoAttributionAnalyzer.Analyze(
            startedAt,
            startedAt,
            [],
            maxOwnersPerDisk: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskIoAttributionAnalyzer.Analyze(
            startedAt,
            startedAt,
            [],
            maxOwnersPerDisk: DiskIoAttributionAnalyzer.MaximumOwnersPerDisk + 1));
    }
}
