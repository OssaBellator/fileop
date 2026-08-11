namespace FileOp.Core.Storage;

public enum StorageReviewProvenance
{
    Downloads,
    UserTemp,
}

public enum StorageReviewLocationStatus
{
    Available,
    OutsideActiveVolume,
    Unavailable,
}

public enum StorageReviewReason
{
    OldInstallerPackage,
    OldArchiveOrDiskImage,
    OldUserTempFile,
}

public sealed record StorageReviewCandidate(
    string Path,
    string Name,
    string Extension,
    StorageReviewProvenance Provenance,
    StorageReviewReason Reason,
    string RuleId,
    long LogicalBytes,
    long? AllocatedBytes,
    DateTimeOffset LastWriteTime)
{
    public long MeasuredBytes => AllocatedBytes ?? LogicalBytes;
}

public sealed record StorageKnownLocationReview(
    StorageReviewProvenance Provenance,
    StorageReviewLocationStatus Status,
    string RootPath,
    string Detail,
    StorageOptimizationPolicy SourcePolicy,
    int SourceStaleCandidateCount,
    bool SourceMayBeTruncated,
    IReadOnlyList<StorageReviewCandidate> Candidates)
{
    public long CandidateMeasuredBytes
    {
        get
        {
            long total = 0;
            foreach (var candidate in Candidates)
            {
                var value = Math.Max(0, candidate.MeasuredBytes);
                total = total > long.MaxValue - value
                    ? long.MaxValue
                    : total + value;
            }

            return total;
        }
    }
}

public sealed record StorageKnownLocationReviewSnapshot(
    DateTimeOffset CapturedAtUtc,
    string ActiveVolumeRootPath,
    IReadOnlyList<StorageKnownLocationReview> Locations)
{
    public long CandidateMeasuredBytes
    {
        get
        {
            long total = 0;
            foreach (var location in Locations)
            {
                var value = Math.Max(0, location.CandidateMeasuredBytes);
                total = total > long.MaxValue - value
                    ? long.MaxValue
                    : total + value;
            }

            return total;
        }
    }
}

public static class StorageKnownLocationReviewClassifier
{
    public const string DownloadsInstallerRuleId = "downloads.old-package-extension.v1";
    public const string DownloadsArchiveRuleId = "downloads.old-archive-extension.v1";
    public const string UserTempRuleId = "user-temp.old-large-file.v1";

    private static readonly HashSet<string> InstallerPackageExtensions = new(
        [".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msu"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ArchiveExtensions = new(
        [".zip", ".7z", ".rar", ".iso", ".tar", ".gz", ".tgz", ".bz2", ".xz"],
        StringComparer.OrdinalIgnoreCase);

    public static StorageKnownLocationReview Classify(
        StorageOptimizationAnalysis analysis,
        StorageReviewProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (provenance is not StorageReviewProvenance.Downloads and not StorageReviewProvenance.UserTemp)
        {
            throw new ArgumentOutOfRangeException(nameof(provenance), provenance, "Unsupported storage-review provenance.");
        }

        var candidates = new List<StorageReviewCandidate>();
        foreach (var source in analysis.StaleLargeFiles)
        {
            var classified = provenance switch
            {
                StorageReviewProvenance.Downloads => ClassifyDownloads(source),
                StorageReviewProvenance.UserTemp => ClassifyUserTemp(source),
                _ => throw new UnreachableException(),
            };
            if (classified is not null)
            {
                candidates.Add(classified);
            }
        }

        return new StorageKnownLocationReview(
            provenance,
            StorageReviewLocationStatus.Available,
            analysis.RootPath,
            CreateAvailableDetail(analysis, provenance),
            analysis.Policy,
            analysis.StaleLargeFiles.Count,
            analysis.StaleLargeFiles.Count >= analysis.Policy.MaxStaleLargeFiles,
            candidates.ToArray());
    }

    public static StorageKnownLocationReview CreateUnavailable(
        StorageReviewProvenance provenance,
        StorageReviewLocationStatus status,
        string rootPath,
        string detail,
        StorageOptimizationPolicy? sourcePolicy = null)
    {
        if (status == StorageReviewLocationStatus.Available)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new StorageKnownLocationReview(
            provenance,
            status,
            rootPath,
            detail,
            sourcePolicy ?? StorageOptimizationPolicy.Default,
            0,
            false,
            Array.Empty<StorageReviewCandidate>());
    }

    private static StorageReviewCandidate? ClassifyDownloads(
        StorageOptimizationFileCandidate source)
    {
        if (InstallerPackageExtensions.Contains(source.Extension))
        {
            return FromSource(
                source,
                StorageReviewProvenance.Downloads,
                StorageReviewReason.OldInstallerPackage,
                DownloadsInstallerRuleId);
        }

        if (ArchiveExtensions.Contains(source.Extension))
        {
            return FromSource(
                source,
                StorageReviewProvenance.Downloads,
                StorageReviewReason.OldArchiveOrDiskImage,
                DownloadsArchiveRuleId);
        }

        return null;
    }

    private static StorageReviewCandidate ClassifyUserTemp(
        StorageOptimizationFileCandidate source) =>
        FromSource(
            source,
            StorageReviewProvenance.UserTemp,
            StorageReviewReason.OldUserTempFile,
            UserTempRuleId);

    private static StorageReviewCandidate FromSource(
        StorageOptimizationFileCandidate source,
        StorageReviewProvenance provenance,
        StorageReviewReason reason,
        string ruleId) =>
        new(
            source.Path,
            source.Name,
            source.Extension,
            provenance,
            reason,
            ruleId,
            source.LogicalBytes,
            source.AllocatedBytes,
            source.LastWriteTime);

    private static string CreateAvailableDetail(
        StorageOptimizationAnalysis analysis,
        StorageReviewProvenance provenance)
    {
        var scope = provenance == StorageReviewProvenance.Downloads
            ? "Downloads package/archive rules were"
            : "The user Temp location rule was";
        return
            $"{scope} applied only to the native Optimize stale-large candidate set " +
            $"(at least {analysis.Policy.LargeFileMinimumBytes:N0} measured bytes and {analysis.Policy.StaleAgeDays:N0} days old). " +
            "Location, age and extension are review evidence only; they do not establish that a file is safe to delete.";
    }
}
