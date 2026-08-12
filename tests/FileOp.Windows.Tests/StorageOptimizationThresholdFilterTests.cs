using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageOptimizationThresholdFilterTests
{
    [TestMethod]
    public void StricterThresholdsFilterWithoutReorderingEvidence()
    {
        var asOf = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var policy = StorageOptimizationPolicy.Default;
        var largest = new[]
        {
            File("large.bin", 4L * 1024 * 1024 * 1024, asOf.AddDays(-10)),
            File("medium.bin", 1024L * 1024 * 1024, asOf.AddDays(-400)),
            File("baseline.bin", policy.LargeFileMinimumBytes, asOf.AddDays(-800)),
        };
        var stale = largest.Skip(1).ToArray();
        var groups = new[]
        {
            Group(2L * 1024 * 1024 * 1024, 3),
            Group(256L * 1024 * 1024, 4),
            Group(policy.SameSizeMinimumBytes, 5),
        };
        var analysis = new StorageOptimizationAnalysis(
            @"C:\",
            asOf,
            policy,
            largest,
            stale,
            groups);
        var thresholds = new StorageOptimizationDisplayThresholds(
            LargeFileMinimumBytes: 1024L * 1024 * 1024,
            SameSizeMinimumBytes: 256L * 1024 * 1024,
            StaleAgeDays: 365);

        var filtered = StorageOptimizationThresholdFilter.Apply(analysis, thresholds);

        CollectionAssert.AreEqual(
            new[] { "large.bin", "medium.bin" },
            filtered.LargestFiles.Select(static file => file.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { "medium.bin", "baseline.bin" },
            filtered.StaleLargeFiles.Select(static file => file.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { groups[0], groups[1] },
            filtered.SameSizeCandidateGroups.ToArray());
        Assert.AreEqual(
            groups[0].PotentialLogicalSavingsUpperBound + groups[1].PotentialLogicalSavingsUpperBound,
            filtered.SameSizePotentialLogicalSavingsUpperBound);
    }

    [TestMethod]
    public void BaselineThresholdsPreserveCurrentBoundedAnalysis()
    {
        var analysis = Analysis();
        var thresholds = StorageOptimizationDisplayThresholds.FromAnalysis(analysis);

        var filtered = StorageOptimizationThresholdFilter.Apply(analysis, thresholds);

        CollectionAssert.AreEqual(analysis.LargestFiles.ToArray(), filtered.LargestFiles.ToArray());
        CollectionAssert.AreEqual(analysis.StaleLargeFiles.ToArray(), filtered.StaleLargeFiles.ToArray());
        CollectionAssert.AreEqual(analysis.SameSizeCandidateGroups.ToArray(), filtered.SameSizeCandidateGroups.ToArray());
    }

    [TestMethod]
    public void LooserLargeThresholdIsRejected()
    {
        var analysis = Analysis();
        var thresholds = StorageOptimizationDisplayThresholds.FromAnalysis(analysis) with
        {
            LargeFileMinimumBytes = analysis.Policy.LargeFileMinimumBytes - 1,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StorageOptimizationThresholdFilter.Apply(analysis, thresholds));
    }

    [TestMethod]
    public void LooserSameSizeThresholdIsRejected()
    {
        var analysis = Analysis();
        var thresholds = StorageOptimizationDisplayThresholds.FromAnalysis(analysis) with
        {
            SameSizeMinimumBytes = analysis.Policy.SameSizeMinimumBytes - 1,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StorageOptimizationThresholdFilter.Apply(analysis, thresholds));
    }

    [TestMethod]
    public void YoungerStaleAgeIsRejected()
    {
        var analysis = Analysis();
        var thresholds = StorageOptimizationDisplayThresholds.FromAnalysis(analysis) with
        {
            StaleAgeDays = analysis.Policy.StaleAgeDays - 1,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StorageOptimizationThresholdFilter.Apply(analysis, thresholds));
    }

    [TestMethod]
    public void ExtremeStaleAgeClampsWithoutDateOverflow()
    {
        var analysis = Analysis();
        var thresholds = StorageOptimizationDisplayThresholds.FromAnalysis(analysis) with
        {
            StaleAgeDays = int.MaxValue,
        };

        var filtered = StorageOptimizationThresholdFilter.Apply(analysis, thresholds);

        Assert.AreEqual(0, filtered.StaleLargeFiles.Count);
    }

    [TestMethod]
    public void SameSizePotentialSavingsSaturate()
    {
        var policy = StorageOptimizationPolicy.Default;
        var analysis = new StorageOptimizationAnalysis(
            @"C:\",
            DateTimeOffset.UnixEpoch,
            policy,
            [],
            [],
            [
                new StorageSameSizeCandidateGroup(
                    policy.SameSizeMinimumBytes,
                    2,
                    long.MaxValue,
                    []),
                new StorageSameSizeCandidateGroup(
                    policy.SameSizeMinimumBytes,
                    2,
                    long.MaxValue,
                    []),
            ]);
        var thresholds = StorageOptimizationDisplayThresholds.FromAnalysis(analysis);

        var filtered = StorageOptimizationThresholdFilter.Apply(analysis, thresholds);

        Assert.AreEqual(long.MaxValue, filtered.SameSizePotentialLogicalSavingsUpperBound);
    }

    private static StorageOptimizationAnalysis Analysis()
    {
        var asOf = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var policy = StorageOptimizationPolicy.Default;
        return new StorageOptimizationAnalysis(
            @"C:\",
            asOf,
            policy,
            [File("large.bin", policy.LargeFileMinimumBytes, asOf.AddDays(-policy.StaleAgeDays))],
            [File("stale.bin", policy.LargeFileMinimumBytes, asOf.AddDays(-policy.StaleAgeDays - 1))],
            [Group(policy.SameSizeMinimumBytes, 2)]);
    }

    private static StorageOptimizationFileCandidate File(
        string name,
        long measuredBytes,
        DateTimeOffset lastWrite) =>
        new(
            Path.Combine(@"C:\", name),
            name,
            Path.GetExtension(name),
            StorageFileCategory.Other,
            measuredBytes,
            measuredBytes,
            lastWrite);

    private static StorageSameSizeCandidateGroup Group(long bytesPerFile, int count) =>
        new(
            bytesPerFile,
            count,
            bytesPerFile * Math.Max(0, count - 1),
            []);
}
