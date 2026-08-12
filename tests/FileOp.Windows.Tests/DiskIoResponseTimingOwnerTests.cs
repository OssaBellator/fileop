using FileOp.Core.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DiskIoResponseTimingOwnerTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 11, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddSeconds(1);

    [TestMethod]
    public void StableAndUnresolvedOwnerEvidenceIsAccepted()
    {
        var owner = new DiskIoProcessIdentity(
            71,
            StartedAt.AddMinutes(-1),
            "worker.exe");
        var owned = Timing() with { Owner = owner };
        var unresolved = Timing();

        var result = DiskIoResponseTimingAnalyzer.Analyze(
            StartedAt,
            EndedAt,
            [owned, unresolved]);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(2L, result[0].SampleCount);
    }

    [TestMethod]
    public void InvalidOwnerProcessIdFailsClosed()
    {
        var timing = Timing() with
        {
            Owner = new DiskIoProcessIdentity(0, StartedAt.AddMinutes(-1), "worker.exe"),
        };

        Assert.Throws<InvalidDataException>(() =>
            DiskIoResponseTimingAnalyzer.Analyze(StartedAt, EndedAt, [timing]));
    }

    [TestMethod]
    public void OwnerStartingAfterCompletionFailsClosed()
    {
        var timing = Timing() with
        {
            Owner = new DiskIoProcessIdentity(71, StartedAt.AddMilliseconds(101), "worker.exe"),
        };

        Assert.Throws<InvalidDataException>(() =>
            DiskIoResponseTimingAnalyzer.Analyze(StartedAt, EndedAt, [timing]));
    }

    [TestMethod]
    public void WhitespaceOwnerImageNameFailsClosed()
    {
        var timing = Timing() with
        {
            Owner = new DiskIoProcessIdentity(71, StartedAt.AddMinutes(-1), "   "),
        };

        Assert.Throws<InvalidDataException>(() =>
            DiskIoResponseTimingAnalyzer.Analyze(StartedAt, EndedAt, [timing]));
    }

    private static DiskIoResponseTimingObservation Timing() =>
        new(
            StartedAt.AddMilliseconds(100),
            2,
            DiskIoOperationKind.Read,
            TimeSpan.FromMilliseconds(1));
}
