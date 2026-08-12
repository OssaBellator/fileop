using FileOp.Core.Operations;
using FileOp.Windows.Operations;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationPreflightTests
{
    [TestMethod]
    public void IntentSnapshotsEntriesAndRejectsEmptySelection()
    {
        var entries = new List<FileOperationEntry>
        {
            new(@"C:\Source\a.txt", "a.txt", IsDirectory: false),
        };
        var intent = new FileDeleteOperationIntent(
            "left",
            Guid.NewGuid(),
            @"C:\Source",
            entries);
        entries.Clear();

        Assert.AreEqual(1, intent.Entries.Count);
        Assert.AreEqual("a.txt", intent.Entries[0].Name);
        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                @"C:\Source",
                Array.Empty<FileOperationEntry>()));
    }

    [TestMethod]
    public void PlanRejectsEmptyIdAndNormalizesQueuedTimestamp()
    {
        var intent = Intent(new FileOperationEntry(@"C:\Source\a.txt", "a.txt", false));
        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationPlan(Guid.Empty, DateTimeOffset.UtcNow, intent));

        var local = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(10));
        var plan = new FileDeleteOperationPlan(Guid.NewGuid(), local, intent);
        Assert.AreEqual(TimeSpan.Zero, plan.QueuedAtUtc.Offset);
        Assert.AreEqual(local.UtcDateTime, plan.QueuedAtUtc.UtcDateTime);
    }

    [TestMethod]
    public void ResultStatusMustMatchCapturedItemDecisionsAndFileEvidence()
    {
        var plan = Plan(new FileOperationEntry(@"C:\Source\a.txt", "a.txt", false));
        var sourceRoot = Inspection(@"C:\Source", FileOperationPathState.Directory);
        var source = Inspection(@"C:\Source\a.txt", FileOperationPathState.File);
        var blocked = new FileDeleteOperationPreflightItem(
            plan.Intent.Entries[0],
            source,
            FileDeleteOperationPreflightDecision.Blocked,
            "blocked");

        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationPreflightResult(
                plan,
                sourceRoot,
                new[] { blocked },
                FileDeleteOperationPreflightStatus.ReadyForFurtherReview,
                "invalid"));
        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationPreflightResult(
                plan,
                sourceRoot,
                Array.Empty<FileDeleteOperationPreflightItem>(),
                FileDeleteOperationPreflightStatus.ReadyForFurtherReview,
                "invalid"));

        var directoryEntry = new FileOperationEntry(@"C:\Source\Folder", "Folder", true);
        var directoryPlan = Plan(directoryEntry);
        var invalidReadyDirectory = new FileDeleteOperationPreflightItem(
            directoryEntry,
            Inspection(directoryEntry.Path, FileOperationPathState.Directory),
            FileDeleteOperationPreflightDecision.ReadyForFurtherReview,
            "invalid ready directory");
        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationPreflightResult(
                directoryPlan,
                sourceRoot,
                new[] { invalidReadyDirectory },
                FileDeleteOperationPreflightStatus.ReadyForFurtherReview,
                "invalid"));

        var invalidReadyReparse = new FileDeleteOperationPreflightItem(
            plan.Intent.Entries[0],
            Inspection(@"C:\Source\a.txt", FileOperationPathState.File, reparse: true),
            FileDeleteOperationPreflightDecision.ReadyForFurtherReview,
            "invalid ready reparse");
        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationPreflightResult(
                plan,
                sourceRoot,
                new[] { invalidReadyReparse },
                FileDeleteOperationPreflightStatus.ReadyForFurtherReview,
                "invalid"));
    }

    [TestMethod]
    public async Task MatchingDirectFileIsReadyForFurtherReviewButNeverAuthorized()
    {
        var entry = new FileOperationEntry(@"C:\Source\a.txt", "a.txt", false);
        var validator = new WindowsFileDeleteOperationPreflightValidator(
            new FakeProbe(new Dictionary<string, FileOperationPathInspection>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Source"] = Inspection(@"C:\Source", FileOperationPathState.Directory),
                [@"C:\Source\a.txt"] = Inspection(@"C:\Source\a.txt", FileOperationPathState.File),
            }));

        var result = await validator.ValidateAsync(Plan(entry));

        Assert.AreEqual(FileDeleteOperationPreflightStatus.ReadyForFurtherReview, result.Status);
        Assert.AreEqual(1, result.ReadyForFurtherReviewCount);
        Assert.AreEqual(0, result.BlockedCount);
        Assert.IsTrue(result.IsReadyForFurtherReview);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        StringAssert.Contains(result.Summary, "not authorization to delete");
    }

    [TestMethod]
    public async Task DirectoryEntryIsBlockedBeforeEntryProbe()
    {
        var probe = new FakeProbe(new Dictionary<string, FileOperationPathInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Source"] = Inspection(@"C:\Source", FileOperationPathState.Directory),
        });
        var result = await new WindowsFileDeleteOperationPreflightValidator(probe)
            .ValidateAsync(Plan(new FileOperationEntry(@"C:\Source\Folder", "Folder", true)));

        Assert.AreEqual(FileDeleteOperationPreflightStatus.Blocked, result.Status);
        Assert.AreEqual(1, result.BlockedCount);
        Assert.AreEqual(1, probe.Calls);
        StringAssert.Contains(result.Items[0].Message, "Directory deletion is not part");
        Assert.IsFalse(result.DeleteMutationAuthorized);
    }

    [TestMethod]
    public async Task MissingChangedOrReparseSourceFailsClosed()
    {
        foreach (var inspection in new[]
        {
            Inspection(@"C:\Source\a.txt", FileOperationPathState.Missing),
            Inspection(@"C:\Source\a.txt", FileOperationPathState.Directory),
            Inspection(@"C:\Source\a.txt", FileOperationPathState.File, reparse: true),
            Inspection(@"C:\Source\a.txt", FileOperationPathState.Inaccessible),
            Inspection(@"C:\Source\a.txt", FileOperationPathState.Error),
        })
        {
            var entry = new FileOperationEntry(@"C:\Source\a.txt", "a.txt", false);
            var validator = new WindowsFileDeleteOperationPreflightValidator(
                new FakeProbe(new Dictionary<string, FileOperationPathInspection>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"C:\Source"] = Inspection(@"C:\Source", FileOperationPathState.Directory),
                    [@"C:\Source\a.txt"] = inspection,
                }));

            var result = await validator.ValidateAsync(Plan(entry));
            Assert.AreEqual(FileDeleteOperationPreflightStatus.Blocked, result.Status);
            Assert.AreEqual(1, result.BlockedCount);
            Assert.IsFalse(result.DeleteMutationAuthorized);
        }
    }

    [TestMethod]
    public async Task SourceRootReparseFailsBeforeEntryProbe()
    {
        var probe = new FakeProbe(new Dictionary<string, FileOperationPathInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Source"] = Inspection(@"C:\Source", FileOperationPathState.Directory, reparse: true),
        });
        var result = await new WindowsFileDeleteOperationPreflightValidator(probe)
            .ValidateAsync(Plan(new FileOperationEntry(@"C:\Source\a.txt", "a.txt", false)));

        Assert.AreEqual(FileDeleteOperationPreflightStatus.Blocked, result.Status);
        Assert.AreEqual(1, probe.Calls);
        Assert.AreEqual(0, result.Items.Count);
        Assert.IsFalse(result.DeleteMutationAuthorized);
    }

    [TestMethod]
    public async Task EntryMustRemainExactDirectChildWithCapturedLeafName()
    {
        var rootOnly = new FakeProbe(new Dictionary<string, FileOperationPathInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Source"] = Inspection(@"C:\Source", FileOperationPathState.Directory),
        });
        var validator = new WindowsFileDeleteOperationPreflightValidator(rootOnly);

        var nested = await validator.ValidateAsync(Plan(
            new FileOperationEntry(@"C:\Source\Nested\a.txt", "a.txt", false)));
        Assert.AreEqual(FileDeleteOperationPreflightStatus.Blocked, nested.Status);

        var renamed = await validator.ValidateAsync(Plan(
            new FileOperationEntry(@"C:\Source\a.txt", "different.txt", false)));
        Assert.AreEqual(FileDeleteOperationPreflightStatus.Blocked, renamed.Status);

        var stream = await validator.ValidateAsync(Plan(
            new FileOperationEntry(@"C:\Source\a.txt:stream", "a.txt:stream", false)));
        Assert.AreEqual(FileDeleteOperationPreflightStatus.Blocked, stream.Status);
        Assert.AreEqual(3, rootOnly.Calls);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesBeforeProbe()
    {
        var probe = new FakeProbe(new Dictionary<string, FileOperationPathInspection>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new WindowsFileDeleteOperationPreflightValidator(probe).ValidateAsync(
                Plan(new FileOperationEntry(@"C:\Source\a.txt", "a.txt", false)),
                cancellation.Token));
        Assert.AreEqual(0, probe.Calls);
    }

    private static FileDeleteOperationIntent Intent(FileOperationEntry entry) =>
        new("left", Guid.NewGuid(), @"C:\Source", new[] { entry });

    private static FileDeleteOperationPlan Plan(FileOperationEntry entry) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, Intent(entry));

    private static FileOperationPathInspection Inspection(
        string path,
        FileOperationPathState state,
        bool reparse = false) =>
        new(path, state, reparse);

    private sealed class FakeProbe(
        IReadOnlyDictionary<string, FileOperationPathInspection> inspections)
        : IFileOperationPathProbe
    {
        public int Calls { get; private set; }

        public ValueTask<FileOperationPathInspection> InspectAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (!inspections.TryGetValue(path, out var inspection))
            {
                throw new AssertFailedException($"Unexpected probe path: {path}");
            }
            return ValueTask.FromResult(inspection);
        }
    }
}
