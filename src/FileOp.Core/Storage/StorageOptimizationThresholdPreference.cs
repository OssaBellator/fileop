namespace FileOp.Core.Storage;

public sealed record StorageOptimizationThresholdPreference(
    int LargeFileMultiplier,
    int SameSizeMultiplier,
    int StaleAgeMultiplier)
{
    public static StorageOptimizationThresholdPreference Baseline { get; } = new(1, 1, 1);
}

public static class StorageOptimizationThresholdPreferencePolicy
{
    private static readonly int[] SupportedSizeMultipliers = [1, 2, 4, 8];
    private static readonly int[] SupportedStaleAgeMultipliers = [1, 2, 4, 6];

    public static bool IsSupported(StorageOptimizationThresholdPreference preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        return SupportedSizeMultipliers.Contains(preference.LargeFileMultiplier) &&
            SupportedSizeMultipliers.Contains(preference.SameSizeMultiplier) &&
            SupportedStaleAgeMultipliers.Contains(preference.StaleAgeMultiplier);
    }

    public static StorageOptimizationDisplayThresholds Resolve(
        StorageOptimizationAnalysis analysis,
        StorageOptimizationThresholdPreference? preference)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var effective = preference is not null && IsSupported(preference)
            ? preference
            : StorageOptimizationThresholdPreference.Baseline;
        var thresholds = new StorageOptimizationDisplayThresholds(
            SaturatingMultiply(
                analysis.Policy.LargeFileMinimumBytes,
                effective.LargeFileMultiplier),
            SaturatingMultiply(
                analysis.Policy.SameSizeMinimumBytes,
                effective.SameSizeMultiplier),
            SaturatingMultiply(
                analysis.Policy.StaleAgeDays,
                effective.StaleAgeMultiplier));
        StorageOptimizationThresholdFilter.ValidateAgainstAnalysis(analysis, thresholds);
        return thresholds;
    }

    public static StorageOptimizationThresholdPreference FromThresholds(
        StorageOptimizationAnalysis analysis,
        StorageOptimizationDisplayThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(thresholds);
        StorageOptimizationThresholdFilter.ValidateAgainstAnalysis(analysis, thresholds);

        return new StorageOptimizationThresholdPreference(
            FindMultiplier(
                analysis.Policy.LargeFileMinimumBytes,
                thresholds.LargeFileMinimumBytes,
                SupportedSizeMultipliers,
                "large-file"),
            FindMultiplier(
                analysis.Policy.SameSizeMinimumBytes,
                thresholds.SameSizeMinimumBytes,
                SupportedSizeMultipliers,
                "same-size"),
            FindMultiplier(
                analysis.Policy.StaleAgeDays,
                thresholds.StaleAgeDays,
                SupportedStaleAgeMultipliers,
                "stale-age"));
    }

    private static int FindMultiplier(
        long baseline,
        long value,
        IReadOnlyList<int> supported,
        string description)
    {
        foreach (var multiplier in supported)
        {
            if (SaturatingMultiply(baseline, multiplier) == value)
            {
                return multiplier;
            }
        }

        throw new ArgumentException(
            $"The {description} threshold is not one of the supported policy-relative display values.",
            nameof(value));
    }

    private static int FindMultiplier(
        int baseline,
        int value,
        IReadOnlyList<int> supported,
        string description)
    {
        foreach (var multiplier in supported)
        {
            if (SaturatingMultiply(baseline, multiplier) == value)
            {
                return multiplier;
            }
        }

        throw new ArgumentException(
            $"The {description} threshold is not one of the supported policy-relative display values.",
            nameof(value));
    }

    internal static long SaturatingMultiply(long value, int multiplier) =>
        value > long.MaxValue / multiplier
            ? long.MaxValue
            : value * multiplier;

    internal static int SaturatingMultiply(int value, int multiplier) =>
        value > int.MaxValue / multiplier
            ? int.MaxValue
            : value * multiplier;
}
