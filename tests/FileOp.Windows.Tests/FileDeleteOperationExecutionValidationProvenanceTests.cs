using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationExecutionValidationProvenanceTests
{
    [TestMethod]
    public void ResultBindsRootAndEveryItemToExactCapturedPlan()
    {
        var first = new FileOperationEntry(@"C:\Users\Alice\Temp\a.tmp", "a.tmp", false);
        var second = new FileOperationEntry(@"C:\Users\Alice\Temp\b.tmp", "b.tmp", false);
        var plan = new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                @"C:\Users\Alice\Temp",
                [first, second]));
        var root = CanonicalDirectory(
            @"C:\Users\Alice\Temp",
            @"C:\Users\Alice\Temp",
            new FileIdentity(1, 10));
        var firstReady = Ready(first, new FileIdentity(1, 11));
        var secondReady = Ready(second, new FileIdentity(1, 12));

        var valid = new FileDeleteOperationExecutionValidationResult(
            plan,
            root,
            [firstReady, secondReady],
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "valid exact-plan evidence");
        Assert.IsTrue(valid.CanRequestAuthorizationReview);
        Assert.IsFalse(valid.DeleteMutationAuthorized);

        Assert.Throws<ArgumentException>(() =>
            new FileDeleteOperationExecutionValidationResult(
                plan,
                root,
                [firstReady],
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                DateTimeOffset.UtcNow,
                "partial evidence"));

        Assert.Throws<ArgumentException>(() =>
            new FileDeleteOperationExecutionValidationResult(
                plan,
                root,
                [secondReady, firstReady],
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                DateTimeOffset.UtcNow,
                "reordered evidence"));

        var substitutedEntry = new FileOperationEntry(
            @"C:\Users\Alice\Temp\other.tmp",
            "other.tmp",
            false);
        var substituted = Ready(substitutedEntry, new FileIdentity(1, 13));
        Assert.Throws<ArgumentException>(() =>
            new FileDeleteOperationExecutionValidationResult(
                plan,
                root,
                [substituted, secondReady],
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                DateTimeOffset.UtcNow,
                "substituted evidence"));

        var wrongRequestedSource = firstReady with
        {
            Source = new FileOperationCanonicalPath(
                @"C:\Users\Alice\Temp\other.tmp",
                first.Path,
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                new FileIdentity(1, 11)),
        };
        Assert.Throws<ArgumentException>(() =>
            new FileDeleteOperationExecutionValidationResult(
                plan,
                root,
                [wrongRequestedSource, secondReady],
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                DateTimeOffset.UtcNow,
                "cross-entry source evidence"));

        var wrongRequestedRoot = CanonicalDirectory(
            @"C:\Users\Alice\Other",
            @"C:\Users\Alice\Temp",
            new FileIdentity(1, 10));
        Assert.Throws<ArgumentException>(() =>
            new FileDeleteOperationExecutionValidationResult(
                plan,
                wrongRequestedRoot,
                Array.Empty<FileDeleteOperationExecutionValidationItem>(),
                FileDeleteOperationExecutionValidationStatus.Blocked,
                DateTimeOffset.UtcNow,
                "cross-plan root evidence"));
    }

    [TestMethod]
    public void RootLevelBlockMayRemainItemlessWhenRootEvidenceBelongsToPlan()
    {
        var entry = new FileOperationEntry(@"C:\Windows\Temp\a.tmp", "a.tmp", false);
        var plan = new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                @"C:\Windows\Temp",
                [entry]));
        var root = CanonicalDirectory(
            @"C:\Windows\Temp",
            @"C:\Windows\Temp",
            new FileIdentity(1, 20));

        var blocked = new FileDeleteOperationExecutionValidationResult(
            plan,
            root,
            Array.Empty<FileDeleteOperationExecutionValidationItem>(),
            FileDeleteOperationExecutionValidationStatus.Blocked,
            DateTimeOffset.UtcNow,
            "protected root blocks before entry validation");

        Assert.AreEqual(0, blocked.Items.Count);
        Assert.IsFalse(blocked.CanRequestAuthorizationReview);
        Assert.IsFalse(blocked.DeleteMutationAuthorized);
    }

    private static FileDeleteOperationExecutionValidationItem Ready(
        FileOperationEntry entry,
        FileIdentity identity) =>
        new(
            entry,
            new FileOperationCanonicalPath(
                entry.Path,
                entry.Path,
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                identity),
            FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
            "ready for authorization review");

    private static FileOperationCanonicalPath CanonicalDirectory(
        string requestedPath,
        string canonicalPath,
        FileIdentity identity) =>
        new(
            requestedPath,
            canonicalPath,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            identity);
}
