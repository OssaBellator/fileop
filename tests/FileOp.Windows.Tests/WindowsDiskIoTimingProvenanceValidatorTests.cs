using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoTimingProvenanceValidatorTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 11, 3, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ExactTimestampDiskOperationAndOwnerMatchPasses()
    {
        var owner = Owner(41);
        var observation = Observation(owner);
        var timing = Timing(owner);

        WindowsDiskIoTimingProvenanceValidator.Validate([observation], [timing]);
    }

    [TestMethod]
    public void CountMismatchFailsClosed()
    {
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTimingProvenanceValidator.Validate(
                [Observation(Owner(41))],
                Array.Empty<DiskIoResponseTimingObservation>()));
    }

    [TestMethod]
    public void TimestampDiskAndOperationMismatchesFailClosed()
    {
        var owner = Owner(41);
        var observation = Observation(owner);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTimingProvenanceValidator.Validate(
                [observation],
                [Timing(owner) with { Timestamp = At.AddTicks(1) }]));
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTimingProvenanceValidator.Validate(
                [observation],
                [Timing(owner) with { PhysicalDiskNumber = 8 }]));
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTimingProvenanceValidator.Validate(
                [observation],
                [Timing(owner) with { Operation = DiskIoOperationKind.Write }]));
    }

    [TestMethod]
    public void ResolvedOwnerMismatchFailsClosed()
    {
        var observation = Observation(Owner(41));

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTimingProvenanceValidator.Validate(
                [observation],
                [Timing(Owner(42))]));
    }

    [TestMethod]
    public void ResolvedAndUnresolvedOwnerMismatchFailsClosed()
    {
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTimingProvenanceValidator.Validate(
                [Observation(Owner(41))],
                [Timing(owner: null)]));
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTimingProvenanceValidator.Validate(
                [Observation(owner: null)],
                [Timing(Owner(41))]));
    }

    [TestMethod]
    public void MatchingUnresolvedOwnerPasses()
    {
        WindowsDiskIoTimingProvenanceValidator.Validate(
            [Observation(owner: null)],
            [Timing(owner: null)]);
    }

    private static DiskIoProcessIdentity Owner(int processId) =>
        new(processId, At.AddMinutes(-2), $"p{processId}.exe");

    private static DiskIoEventObservation Observation(DiskIoProcessIdentity? owner) =>
        new(
            At,
            PhysicalDiskNumber: 3,
            DiskIoOperationKind.Read,
            TransferBytes: 4096,
            owner);

    private static DiskIoResponseTimingObservation Timing(DiskIoProcessIdentity? owner) =>
        new(
            At,
            PhysicalDiskNumber: 3,
            DiskIoOperationKind.Read,
            TimeSpan.FromMilliseconds(2))
        {
            Owner = owner,
        };
}
