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
    public void DownloadsArchiveExtensionsUseArchiveOnlyReviewProvenance()
    {
        var analysis = Analysis(File("archive.zip", ".zip"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual(StorageReviewReason.OldArchive, review.Candidates[0].Reason);
        Assert.AreEqual(StorageKnownLocationReviewClassifier.DownloadsArchiveRuleId, review.Candidates[0].RuleId);
    }

    [TestMethod]
    public void DownloadsDiskImageUsesDistinctReviewProvenance()
    {
        var analysis = Analysis(File("archive.iso", ".iso"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual(StorageReviewReason.OldDiskImage, review.Candidates[0].Reason);
        Assert.AreEqual(StorageKnownLocationReviewClassifier.DownloadsDiskImageRuleId, review.Candidates[0].RuleId);
    }

    [TestMethod]
    public void DownloadsArchiveDiskImageSplitDoesNotWidenCandidateExtensions()
    {
        var analysis = Analysis(
            File("package.msix", ".msix"),
            File("archive.7z", ".7z"),
            File("image.iso", ".iso"),
            File("setup.exe", ".exe"),
            File("disk.img", ".img"),
            File("virtual.vhdx", ".vhdx"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(3, review.Candidates.Count);
        CollectionAssert.AreEqual(
            new[] { "package.msix", "archive.7z", "image.iso" },
            review.Candidates.Select(static candidate => candidate.Name).ToArray());
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
    public void CandidateContainmentAcceptsRootNestedAndCaseEquivalentPaths()
    {
        const string root = @"C:\review.zip";
        var analysis = Analysis(
            root,
            FileAt(root, "review.zip", ".zip"),
            FileAt(@"C:\review.zip\nested\archive.zip", "archive.zip", ".zip"),
            FileAt(@"c:\REVIEW.ZIP\nested\image.iso", "image.iso", ".iso"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        CollectionAssert.AreEqual(
            new[] { "review.zip", "archive.zip", "image.iso" },
            review.Candidates.Select(static candidate => candidate.Name).ToArray());
    }

    [TestMethod]
    public void CandidateContainmentRejectsSiblingOtherDriveAndRelativePaths()
    {
        const string root = @"C:\review";
        var analysis = Analysis(
            root,
            FileAt(@"C:\review\good.work", "good.work", ".work"),
            FileAt(@"C:\review2\sibling.work", "sibling.work", ".work"),
            FileAt(@"D:\review\other.work", "other.work", ".work"),
            FileAt(@"C:relative.work", "relative.work", ".work"),
            FileAt(@"\review\current-drive.work", "current-drive.work", ".work"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.UserTemp);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual("good.work", review.Candidates[0].Name);
    }

    [TestMethod]
    public void CandidateContainmentRejectsMalformedPathWithoutDiscardingValidNeighbor()
    {
        const string root = @"C:\review";
        var analysis = Analysis(
            root,
            FileAt("C:\\review\\bad\0.zip", "bad.zip", ".zip"),
            FileAt(@"C:\review\good.zip", "good.zip", ".zip"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual("good.zip", review.Candidates[0].Name);
    }

    [TestMethod]
    public void CandidateContainmentSupportsUncRootsWithoutSiblingPrefixLeakage()
    {
        const string root = @"\\server\share\review";
        var analysis = Analysis(
            root,
            FileAt(@"\\server\share\review\nested\good.zip", "good.zip", ".zip"),
            FileAt(@"\\server\share\review2\bad.zip", "bad.zip", ".zip"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual("good.zip", review.Candidates[0].Name);
    }

    [TestMethod]
    public void CandidateMetadataRejectsExtensionSpoofWithoutDiscardingValidNeighbor()
    {
        const string root = @"C:\review";
        var analysis = Analysis(
            root,
            FileAt(@"C:\review\photo.jpg", "photo.jpg", ".zip"),
            FileAt(@"C:\review\good.zip", "good.zip", ".zip"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual("good.zip", review.Candidates[0].Name);
    }

    [TestMethod]
    public void CandidateMetadataRejectsMismatchedNameForUserTemp()
    {
        const string root = @"C:\review";
        var analysis = Analysis(
            root,
            FileAt(@"C:\review\actual.work", "alias.work", ".work"),
            FileAt(@"C:\review\good.work", "good.work", ".work"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.UserTemp);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual("good.work", review.Candidates[0].Name);
    }

    [TestMethod]
    public void CandidateMetadataComparisonIsCaseInsensitive()
    {
        var analysis = Analysis(
            @"C:\review",
            FileAt(@"C:\review\ARCHIVE.ZIP", "archive.zip", ".zip"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual(StorageReviewReason.OldArchive, review.Candidates[0].Reason);
    }

    [TestMethod]
    public void FilteredCandidatesStillCountTowardUpstreamStaleCap()
    {
        var policy = StorageOptimizationPolicy.Default with { MaxStaleLargeFiles = 2 };
        var analysis = Analysis(
            @"C:\review",
            policy,
            FileAt(@"C:\review\good.zip", "good.zip", ".zip"),
            FileAt(@"C:\review2\bad.zip", "bad.zip", ".zip"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual(2, review.SourceStaleCandidateCount);
        Assert.IsTrue(review.SourceMayBeTruncated);
    }

    [TestMethod]
    public void MetadataFilteredCandidatesStillCountTowardUpstreamStaleCap()
    {
        var policy = StorageOptimizationPolicy.Default with { MaxStaleLargeFiles = 2 };
        var analysis = Analysis(
            @"C:\review",
            policy,
            FileAt(@"C:\review\good.zip", "good.zip", ".zip"),
            FileAt(@"C:\review\photo.jpg", "photo.jpg", ".zip"));

        var review = StorageKnownLocationReviewClassifier.Classify(
            analysis,
            StorageReviewProvenance.Downloads);

        Assert.AreEqual(1, review.Candidates.Count);
        Assert.AreEqual(2, review.SourceStaleCandidateCount);
        Assert.IsTrue(review.SourceMayBeTruncated);
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
        Assert.Throws<ArgumentOutOfRangeException>(() =>
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

        Assert.Throws<ArgumentOutOfRangeException>(() =>
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
        Analysis(@"C:\review", policy, files);

    private static StorageOptimizationAnalysis Analysis(
        string rootPath,
        params StorageOptimizationFileCandidate[] files) =>
        Analysis(rootPath, StorageOptimizationPolicy.Default, files);

    private static StorageOptimizationAnalysis Analysis(
        string rootPath,
        StorageOptimizationPolicy policy,
        params StorageOptimizationFileCandidate[] files) =>
        new(
            rootPath,
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
        FileAt(Path.Combine(@"C:\review", name), name, extension, logicalBytes, allocatedBytes);

    private static StorageOptimizationFileCandidate FileAt(
        string path,
        string name,
        string extension,
        long logicalBytes = 1024,
        long? allocatedBytes = 1024) =>
        new(
            path,
            name,
            extension,
            StorageFileCategory.Other,
            logicalBytes,
            allocatedBytes,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
}
