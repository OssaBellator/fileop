namespace FileOp.Core.Storage;

public sealed record StorageOptimizationDisplayThresholds(
    long LargeFileMinimumBytes,
    long SameSizeMinimumBytes,
    int StaleAgeDays)
{
    public static StorageOptimizationDisplayThresholds FromAnalysis(
        StorageOptimizationAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return new StorageOptimizationDisplayThresholds(
            analysis.Policy.LargeFileMinimumBytes,
            analysis.Policy.SameSizeMinimumBytes,
            analysis.Policy.StaleAgeDays);
    }
}

public sealed record StorageOptimizationFilteredView(
    StorageOptimizationDisplayThresholds Thresholds,
    IReadOnlyList<StorageOptimizationFileCandidate> LargestFiles,
    IReadOnlyList<StorageOptimizationFileCandidate> StaleLargeFiles,
    IReadOnlyList<StorageSameSizeCandidateGroup> SameSizeCandidateGroups)
{
    public long SameSizePotentialLogicalSavingsUpperBound
    {
        get
        {
            long total = 0;
            foreach (var group in SameSizeCandidateGroups)
            {
                var value = Math.Max(0, group.PotentialLogicalSavingsUpperBound);
                total = total > long.MaxValue - value
                    ? long.MaxValue
                    : total + value;
            }

            return total;
        }
    }
}

public static class StorageOptimizationThresholdFilter
{
    public static StorageOptimizationFilteredView Apply(
        StorageOptimizationAnalysis analysis,
        StorageOptimizationDisplayThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(thresholds);
        ValidateAgainstAnalysis(analysis, thresholds);

        var staleCutoff = analysis.AsOfUtc.ToUniversalTime().AddDays(-thresholds.StaleAgeDays);
        var largestFiles = analysis.LargestFiles
            .Where(file => file.MeasuredBytes >= thresholds.LargeFileMinimumBytes)
            .ToArray();
        var staleFiles = analysis.StaleLargeFiles
            .Where(file =>
                file.MeasuredBytes >= thresholds.LargeFileMinimumBytes &&
                file.LastWriteTime.ToUniversalTime() <= staleCutoff)
            .ToArray();
        var sameSizeGroups = analysis.SameSizeCandidateGroups
            .Where(group => group.LogicalBytesPerFile >= thresholds.SameSizeMinimumBytes)
            .ToArray();

        return new StorageOptimizationFilteredView(
            thresholds,
            largestFiles,
            staleFiles,
            sameSizeGroups);
    }

    public static void ValidateAgainstAnalysis(
        StorageOptimizationAnalysis analysis,
        StorageOptimizationDisplayThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(thresholds);

        if (thresholds.LargeFileMinimumBytes < analysis.Policy.LargeFileMinimumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholds),
                thresholds.LargeFileMinimumBytes,
                "The display large-file threshold cannot be lower than the helper analysis threshold because omitted files would make the filtered result incomplete.");
        }
        if (thresholds.SameSizeMinimumBytes < analysis.Policy.SameSizeMinimumBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholds),
                thresholds.SameSizeMinimumBytes,
                "The display same-size threshold cannot be lower than the helper analysis threshold because omitted groups would make the filtered result incomplete.");
        }
        if (thresholds.StaleAgeDays < analysis.Policy.StaleAgeDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(thresholds),
                thresholds.StaleAgeDays,
                "The display stale-age threshold cannot be younger than the helper analysis threshold because omitted files would make the filtered result incomplete.");
        }
    }
}
