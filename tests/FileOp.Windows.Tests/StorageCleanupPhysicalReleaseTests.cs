using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageCleanupPhysicalReleaseTests
{
    private static readonly DateTimeOffset IndexedTime =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void MatchingSingletonEvidenceExposesPhysicalReleaseUpperBoundWithoutAuthorization()
    {
        var preview = Analyze(Current());

        Assert.AreEqual(StorageCleanupReadinessStatus.CurrentEvidenceConsistent, preview.Status);
        Assert.AreEqual(1536L, preview.CurrentAllocatedBytes);
        Assert.AreEqual(1U, preview.CurrentHardLinkCount);
        Assert.AreEqual(1536L, preview.CurrentPhysicalReleaseUpperBoundBytes);
        Assert.IsFalse(preview.CleanupMutationAuthorized);
        StringAssert.Contains(preview.Detail, "one hard link");
    }

    [TestMethod]
    public void MultiLinkEvidenceReportsZeroPerPathPhysicalRelease()
    {
        var preview = Analyze(Current() with { HardLinkCount = 3 });

        Assert.AreEqual(StorageCleanupReadinessStatus.CurrentEvidenceConsistent, preview.Status);
        Assert.AreEqual(3U, preview.CurrentHardLinkCount);
        Assert.AreEqual(0L, preview.CurrentPhysicalReleaseUpperBoundBytes);
        Assert.IsFalse(preview.CleanupMutationAuthorized);
        StringAssert.Contains(preview.Detail, "deleting this one path alone is not evidence");
    }

    [TestMethod]
    public void InvalidAllocationOrHardLinkEvidenceIsUnavailable()
    {
        var zeroLinks = Analyze(Current() with { HardLinkCount = 0 });
        var negativeAllocation = Analyze(Current() with { AllocatedBytes = -1 });

        Assert.AreEqual(StorageCleanupReadinessStatus.Unavailable, zeroLinks.Status);
        Assert.AreEqual(StorageCleanupReadinessStatus.Unavailable, negativeAllocation.Status);
        Assert.AreEqual(0L, zeroLinks.CurrentPhysicalReleaseUpperBoundBytes);
        Assert.AreEqual(0L, negativeAllocation.CurrentPhysicalReleaseUpperBoundBytes);
    }

    [TestMethod]
    public void ChangedCandidateNeverExposesPhysicalReleaseUpperBound()
    {
        var candidate = Candidate();
        var preview = Analyze(Current() with { LogicalBytes = candidate.LogicalBytes + 1 });

        Assert.AreEqual(StorageCleanupReadinessStatus.CandidateChanged, preview.Status);
        Assert.AreEqual(0L, preview.CurrentPhysicalReleaseUpperBoundBytes);
    }

    private static StorageCleanupReadinessPreview Analyze(StorageCleanupCurrentFileEvidence current)
    {
        var candidate = Candidate();
        return StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            new FileOperationCanonicalPath(
                @"C:\Users\A\Downloads",
                @"C:\Users\A\Downloads",
                FileOperationCanonicalPathState.Directory,
                false,
                new FileIdentity(7, 101)),
            new FileOperationCanonicalPath(
                candidate.Path,
                candidate.Path,
                FileOperationCanonicalPathState.File,
                false,
                new FileIdentity(7, 202)),
            current,
            null,
            IndexedTime.AddDays(10));
    }

    private static StorageReviewCandidate Candidate() =>
        new(
            @"C:\Users\A\Downloads\old.zip",
            "old.zip",
            ".zip",
            StorageReviewProvenance.Downloads,
            StorageReviewReason.OldArchiveOrDiskImage,
            StorageKnownLocationReviewClassifier.DownloadsArchiveRuleId,
            1024,
            2048,
            IndexedTime);

    private static StorageCleanupCurrentFileEvidence Current()
    {
        var candidate = Candidate();
        return new StorageCleanupCurrentFileEvidence(
            candidate.Path,
            candidate.Path,
            new FileIdentity(7, 202),
            candidate.LogicalBytes,
            1536,
            1,
            candidate.LastWriteTime,
            false);
    }
}
