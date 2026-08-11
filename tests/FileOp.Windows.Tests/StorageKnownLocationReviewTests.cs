using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageKnownLocationReviewTests
{
    [TestMethod]
    public void DownloadsPackageExtensionsBecomeReviewCandidates()
    {
        var analysis = Analysis(
            File("setup.msi", ".msi", logicalBytes: 900, allocatedBytes: 700));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual(StorageReviewReason.OldInstallerPackage, review.Candidates[0].Reason);
        Assert.AreEqual(StorageKnownLocationReviewClassifier.DownloadsInstallerRuleId, review.Candidates[0].RuleId);
        Assert.AreEqual(700L, review.Candidates[0].MeasuredBytes);
    }

    [TestMethod]
    public void DownloadsArchiveExtensionsBecomeReviewCandidates()
    {
        var analysis = Analysis(File("archive.iso", ".iso"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual(StorageReviewReason.OldArchiveOrDiskImage, review.Candidates[0].Reason);
        Assert.AreEqual(StorageKnownLocationReviewClassifier.DownloadsArchiveRuleId, review.Candidates[0].RuleId);
    }

    [TestMethod]
    public void DownloadsExecutableIsNotInferredToBeInstaller()
    {
        var analysis = Analysis(File("setup.exe", ".exe"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(0, review.Candidates.Count);
    }

    [TestMethod]
    public void DownloadsUnrecognizedExtensionIsIgnored()
    {
        var analysis = Analysis(File("project.data", ".data"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(0, review.Candidates.Count);
    }

    [TestMethod]
    public void UserTempUsesLocationProvenanceWithoutExtensionGuess()
    {
        var source = File("opaque.work", ".work");
        var analysis = Analysis(source);

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.UserTemp);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual(StorageReviewReason.OldUserTempFile, review.Candidates[0].Reason);
        Assert.AreEqual(StorageKnownLocationReviewClassifier.UserTempRuleId, review.Candidates[0].RuleId);
        Assert.AreEqual(source.Path, review.Candidates[0].Path);
    }

    [TestMethod]
    public void ReachingUpstreamStaleCapIsMarkedPotentiallyTruncated()
    {
        var policy = StorageOptimizationPolicy.Default with { MaxStaleLargeFiles = 2 };
        var analysis = Analysis(
            policy,
            File("one.msi", ".msi"),
            File("two.zip", ".zip"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.IsTrue(review.SourceMayBeTruncated);
        Assert.AreEqual(2, review.SourceStaleCandidateCount);
    }

    [TestMethod]
    public void CandidateMeasuredBytesSaturateAcrossLocationAndSnapshot()
    {
        var policy = StorageOptimizationPolicy.Default;
        var analysis = Analysis(
            policy,
            File("one.msi", ".msi", long.MaxValue, long.MaxValue),
            File("two.zip", ".zip", long.MaxValue, long.MaxValue));
        var downloads = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);
        var snapshot = new StorageKnownLocationReviewSnapshot(
            DateTimeOffset.UtcNow,
            @"C:\",
            [downloads, downloads]);

        Assert.AreEqual(long.MaxValue, downloads.CandidateMeasuredBytes);
        Assert.AreEqual(long.MaxValue, snapshot.CandidateMeasuredBytes);
    }

    [TestMethod]
    public void UnavailableFactoryRejectsAvailableStatus()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            StorageKnownLocationReviewClassifier.CreateUnavailable(
                StorageReviewProvenance.Downloads,
                StorageReviewLocationStatus.Available,
                @"C:\Downloads",
                "invalid"));
    }

    [TestMethod]
    public void UnsupportedProvenanceIsRejected()
    {
        var analysis = Analysis(File("setup.msi", ".msi"));

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            StorageKnownLocationReviewClassifier.Classify(
                analysis,
                (StorageReviewProvenance)999));
    }

    private static StorageOptimizationAnalysis Analysis(
        params StorageOptimizationFileCandidate[] files) =>
        Analysis(StorageOptimizationPolicy.Default, files);

    private static StorageOptimizationAnalysis Analysis(
        StorageOptimizationPolicy policy,
        params StorageOptimizationFileCandidate[] files) =>
        new(
            @"C:\review",
            new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero),
            policy,
            files,
            files,
            []);

    private static StorageOptimizationFileCandidate File(
        string name,
        string extension,
        long logicalBytes = 1024,
        long? allocatedBytes = 1024) =>
        new(
            Path.Combine(@"C:\review", name),
            name,
            extension,
            StorageFileCategory.Other,
            logicalBytes,
            allocatedBytes,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
}
