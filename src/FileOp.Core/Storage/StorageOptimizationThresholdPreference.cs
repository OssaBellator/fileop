using System.Collections.Generic;
using System.Linq;

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
    public static IReadOnlyList<int> SupportedSizeMultipliers { get; } =
        Array.AsReadOnly(new[] { 1, 2, 4, 8 });

    public static IReadOnlyList<int> SupportedStaleAgeMultipliers { get; } =
        Array.AsReadOnly(new[] { 1, 2, 4, 6 });

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
        StorageOptimizationThresholdFilter.ValidateAgainstAnalysis(analysis, thresholds);

        return new StorageOptimizationThresholdPreference(
            FindMultiplier(
                analysis.Policy.LargeFileMinimumBytes,
                thresholds.LargeFileMinimumBytes,
                SupportedSizeMultipliers),
            FindMultiplier(
                analysis.Policy.SameSizeMinimumBytes,
                thresholds.SameSizeMinimumBytes,
                SupportedSizeMultipliers),
            FindMultiplier(
                analysis.Policy.StaleAgeDays,
                thresholds.StaleAgeDays,
                SupportedStaleAgeMultipliers));
    }

    private static int FindMultiplier(
        long baseline,
        long threshold,
        IReadOnlyList<int> supportedMultipliers)
    {
        foreach (var multiplier in supportedMultipliers)
        {
            if (SaturatingMultiply(baseline, multiplier) == threshold)
            {
                return multiplier;
            }
        }

        throw new ArgumentException(
            "Threshold is not representable by a supported policy-relative multiplier.",
            nameof(threshold));
    }

    private static int FindMultiplier(
        int baseline,
        int threshold,
        IReadOnlyList<int> supportedMultipliers)
    {
        foreach (var multiplier in supportedMultipliers)
        {
            if (SaturatingMultiply(baseline, multiplier) == threshold)
            {
                return multiplier;
            }
        }

        throw new ArgumentException(
            "Threshold is not representable by a supported policy-relative multiplier.",
            nameof(threshold));
    }

    public static long SaturatingMultiply(long value, int multiplier)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (multiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        }

        return value > long.MaxValue / multiplier
            ? long.MaxValue
            : value * multiplier;
    }

    public static int SaturatingMultiply(int value, int multiplier)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        if (multiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        }

        return value > int.MaxValue / multiplier
            ? int.MaxValue
            : value * multiplier;
    }
}
