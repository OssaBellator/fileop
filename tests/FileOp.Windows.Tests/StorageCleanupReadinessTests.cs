using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageCleanupReadinessTests
{
    private static readonly DateTimeOffset IndexedTime =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void MatchingCurrentEvidenceRemainsNonAuthorizing()
    {
        var candidate = Candidate();
        var preview = Analyze(candidate, Current(candidate));

        Assert.AreEqual(StorageCleanupReadinessStatus.CurrentEvidenceConsistent, preview.Status);
        Assert.IsTrue(preview.CanonicalEvidencePassed);
        Assert.IsFalse(preview.CleanupMutationAuthorized);
        StringAssert.Contains(preview.Detail, "continuity of the same file object since indexing is not proven");
        StringAssert.Contains(preview.Detail, "Deletion is still unavailable");
    }

    [TestMethod]
    public void CurrentSizeChangeInvalidatesIndexedReviewEvidence()
    {
        var candidate = Candidate();
        var preview = Analyze(candidate, Current(candidate) with { LogicalBytes = candidate.LogicalBytes + 1 });

        Assert.AreEqual(StorageCleanupReadinessStatus.CandidateChanged, preview.Status);
        Assert.IsFalse(preview.CleanupMutationAuthorized);
    }

    [TestMethod]
    public void CurrentLastWriteChangeInvalidatesIndexedReviewEvidence()
    {
        var candidate = Candidate();
        var preview = Analyze(
            candidate,
            Current(candidate) with { LastWriteTimeUtc = candidate.LastWriteTime.AddTicks(1) });

        Assert.AreEqual(StorageCleanupReadinessStatus.CandidateChanged, preview.Status);
    }

    [TestMethod]
    public void IdentityChangeBetweenCurrentReadsFailsClosed()
    {
        var candidate = Candidate();
        var preview = Analyze(
            candidate,
            Current(candidate) with { Identity = new FileIdentity(7, 999) });

        Assert.AreEqual(StorageCleanupReadinessStatus.CandidateChanged, preview.Status);
        StringAssert.Contains(preview.Detail, "changed between canonical validation");
    }

    [TestMethod]
    public void CanonicalEscapeIsBlocked()
    {
        var candidate = Candidate();
        var root = CanonicalRoot();
        var canonicalCandidate = CanonicalCandidate(candidate) with
        {
            CanonicalPath = @"C:\Elsewhere\old.zip",
        };

        var preview = StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            root,
            canonicalCandidate,
            Current(candidate) with { CanonicalPath = canonicalCandidate.CanonicalPath },
            null,
            IndexedTime.AddDays(10));

        Assert.AreEqual(StorageCleanupReadinessStatus.Blocked, preview.Status);
    }

    [TestMethod]
    public void CandidateReparsePointIsBlocked()
    {
        var candidate = Candidate();
        var canonicalCandidate = CanonicalCandidate(candidate) with { IsLeafReparsePoint = true };

        var preview = StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            CanonicalRoot(),
            canonicalCandidate,
            null,
            null,
            IndexedTime.AddDays(10));

        Assert.AreEqual(StorageCleanupReadinessStatus.Blocked, preview.Status);
    }

    [TestMethod]
    public void MissingCandidateIsReportedChanged()
    {
        var candidate = Candidate();
        var canonicalCandidate = CanonicalCandidate(candidate) with
        {
            State = FileOperationCanonicalPathState.Missing,
            Identity = null,
        };

        var preview = StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            CanonicalRoot(),
            canonicalCandidate,
            null,
            null,
            IndexedTime.AddDays(10));

        Assert.AreEqual(StorageCleanupReadinessStatus.CandidateChanged, preview.Status);
    }

    [TestMethod]
    public void InaccessibleCandidateIsUnavailable()
    {
        var candidate = Candidate();
        var canonicalCandidate = CanonicalCandidate(candidate) with
        {
            State = FileOperationCanonicalPathState.Inaccessible,
            Identity = null,
            ErrorCode = "AccessDenied",
        };

        var preview = StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            CanonicalRoot(),
            canonicalCandidate,
            null,
            null,
            IndexedTime.AddDays(10));

        Assert.AreEqual(StorageCleanupReadinessStatus.Unavailable, preview.Status);
    }

    [TestMethod]
    public void CrossVolumeResolutionIsBlocked()
    {
        var candidate = Candidate();
        var canonicalCandidate = CanonicalCandidate(candidate) with
        {
            Identity = new FileIdentity(8, 202),
        };

        var preview = StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            CanonicalRoot(),
            canonicalCandidate,
            Current(candidate) with { Identity = canonicalCandidate.Identity!.Value },
            null,
            IndexedTime.AddDays(10));

        Assert.AreEqual(StorageCleanupReadinessStatus.Blocked, preview.Status);
    }

    [TestMethod]
    public void ReviewRootReparsePointIsBlocked()
    {
        var candidate = Candidate();
        var preview = StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            CanonicalRoot() with { IsLeafReparsePoint = true },
            CanonicalCandidate(candidate),
            Current(candidate),
            null,
            IndexedTime.AddDays(10));

        Assert.AreEqual(StorageCleanupReadinessStatus.Blocked, preview.Status);
    }

    [TestMethod]
    public void CurrentMetadataFailureDoesNotBecomeReady()
    {
        var candidate = Candidate();
        var preview = StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            CanonicalRoot(),
            CanonicalCandidate(candidate),
            null,
            "Current metadata unavailable.",
            IndexedTime.AddDays(10));

        Assert.AreEqual(StorageCleanupReadinessStatus.Unavailable, preview.Status);
        Assert.AreEqual("Current metadata unavailable.", preview.Detail);
    }

    [TestMethod]
    public void MismatchedCanonicalRequestedPathsAreBlocked()
    {
        var candidate = Candidate();
        var preview = StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            CanonicalRoot() with { RequestedPath = @"C:\Users\A\Other" },
            CanonicalCandidate(candidate),
            Current(candidate),
            null,
            IndexedTime.AddDays(10));

        Assert.AreEqual(StorageCleanupReadinessStatus.Blocked, preview.Status);
        StringAssert.Contains(preview.Detail, "not bound to the requested known-location root");
    }

    [TestMethod]
    public void MismatchedCurrentRequestedPathIsBlocked()
    {
        var candidate = Candidate();
        var preview = Analyze(
            candidate,
            Current(candidate) with { RequestedPath = @"C:\Users\A\Downloads\other.zip" });

        Assert.AreEqual(StorageCleanupReadinessStatus.Blocked, preview.Status);
        StringAssert.Contains(preview.Detail, "not bound to the requested review candidate path");
    }

    private static StorageCleanupReadinessPreview Analyze(
        StorageReviewCandidate candidate,
        StorageCleanupCurrentFileEvidence current) =>
        StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            @"C:\Users\A\Downloads",
            CanonicalRoot(),
            CanonicalCandidate(candidate),
            current,
            null,
            IndexedTime.AddDays(10));

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

    private static FileOperationCanonicalPath CanonicalRoot() =>
        new(
            @"C:\Users\A\Downloads",
            @"C:\Users\A\Downloads",
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(7, 101));

    private static FileOperationCanonicalPath CanonicalCandidate(StorageReviewCandidate candidate) =>
        new(
            candidate.Path,
            candidate.Path,
            FileOperationCanonicalPathState.File,
            false,
            new FileIdentity(7, 202));

    private static StorageCleanupCurrentFileEvidence Current(StorageReviewCandidate candidate) =>
        new(
            candidate.Path,
            candidate.Path,
            new FileIdentity(7, 202),
            candidate.LogicalBytes,
            candidate.LastWriteTime,
            false);
}
