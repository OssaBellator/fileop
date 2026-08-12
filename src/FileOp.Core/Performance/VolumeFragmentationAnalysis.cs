namespace FileOp.Core.Performance;

public sealed record VolumeFragmentationAnalysisBudget
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(5);

    public static VolumeFragmentationAnalysisBudget Default { get; } =
        new(DefaultTimeout);

    public VolumeFragmentationAnalysisBudget(TimeSpan timeout)
    {
        if (timeout < MinimumTimeout || timeout > MaximumTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                $"Volume fragmentation analysis timeout must be between {MinimumTimeout.TotalSeconds:N0} second and {MaximumTimeout.TotalMinutes:N0} minutes.");
        }

        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}

public enum VolumeFragmentationAnalysisStatus
{
    Completed,
    Unsupported,
    PermissionRequired,
    Cancelled,
    Unavailable,
}

public sealed record VolumeFragmentationEvidence
{
    public VolumeFragmentationEvidence(
        string volumeRoot,
        bool windowsDefragRecommended,
        uint filePercentFragmentation,
        double averageFragmentsPerFile,
        ulong totalFiles,
        ulong totalFragmentedFiles,
        ulong totalFreeSpaceExtents,
        ulong largestFreeSpaceExtentBytes,
        double averageFreeSpacePerExtentBytes,
        ulong volumeSizeBytes,
        ulong usedSpaceBytes,
        ulong freeSpaceBytes)
    {
        VolumeRoot = VolumeFragmentationDriveRoot.RequireCanonical(volumeRoot);
        if (filePercentFragmentation > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(filePercentFragmentation),
                filePercentFragmentation,
                "File fragmentation percentage must be between 0 and 100.");
        }
        if (!double.IsFinite(averageFragmentsPerFile) || averageFragmentsPerFile < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(averageFragmentsPerFile));
        }
        if (!double.IsFinite(averageFreeSpacePerExtentBytes) || averageFreeSpacePerExtentBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(averageFreeSpacePerExtentBytes));
        }
        if (totalFragmentedFiles > totalFiles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalFragmentedFiles),
                totalFragmentedFiles,
                "Fragmented-file count cannot exceed total file count.");
        }
        if (usedSpaceBytes > volumeSizeBytes || freeSpaceBytes > volumeSizeBytes)
        {
            throw new ArgumentException(
                "Used/free space evidence cannot individually exceed the reported volume size.");
        }
        if (largestFreeSpaceExtentBytes > freeSpaceBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(largestFreeSpaceExtentBytes),
                largestFreeSpaceExtentBytes,
                "Largest free-space extent cannot exceed total free space.");
        }

        WindowsDefragRecommended = windowsDefragRecommended;
        FilePercentFragmentation = filePercentFragmentation;
        AverageFragmentsPerFile = averageFragmentsPerFile;
        TotalFiles = totalFiles;
        TotalFragmentedFiles = totalFragmentedFiles;
        TotalFreeSpaceExtents = totalFreeSpaceExtents;
        LargestFreeSpaceExtentBytes = largestFreeSpaceExtentBytes;
        AverageFreeSpacePerExtentBytes = averageFreeSpacePerExtentBytes;
        VolumeSizeBytes = volumeSizeBytes;
        UsedSpaceBytes = usedSpaceBytes;
        FreeSpaceBytes = freeSpaceBytes;
    }

    public string VolumeRoot { get; }
    public bool WindowsDefragRecommended { get; }
    public uint FilePercentFragmentation { get; }
    public double AverageFragmentsPerFile { get; }
    public ulong TotalFiles { get; }
    public ulong TotalFragmentedFiles { get; }
    public ulong TotalFreeSpaceExtents { get; }
    public ulong LargestFreeSpaceExtentBytes { get; }
    public double AverageFreeSpacePerExtentBytes { get; }
    public ulong VolumeSizeBytes { get; }
    public ulong UsedSpaceBytes { get; }
    public ulong FreeSpaceBytes { get; }
}

public sealed record VolumeFragmentationAnalysisResult
{
    public VolumeFragmentationAnalysisResult(
        string volumeRoot,
        VolumeFragmentationAnalysisBudget budget,
        VolumeFragmentationAnalysisStatus status,
        VolumeFragmentationEvidence? evidence,
        uint? providerReturnCode,
        TimeSpan elapsed,
        string detail)
    {
        VolumeRoot = VolumeFragmentationDriveRoot.RequireCanonical(volumeRoot);
        ArgumentNullException.ThrowIfNull(budget);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if ((status == VolumeFragmentationAnalysisStatus.Completed) != (evidence is not null))
        {
            throw new ArgumentException(
                "Completed fragmentation analysis requires evidence; non-completed results cannot carry evidence.",
                nameof(evidence));
        }
        if (status == VolumeFragmentationAnalysisStatus.Completed && providerReturnCode != 0)
        {
            throw new ArgumentException(
                "Completed fragmentation analysis requires provider return code 0.",
                nameof(providerReturnCode));
        }
        if (evidence is not null && !string.Equals(evidence.VolumeRoot, VolumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Fragmentation evidence volume root must match its result.",
                nameof(evidence));
        }

        Budget = budget;
        Status = status;
        Evidence = evidence;
        ProviderReturnCode = providerReturnCode;
        Elapsed = elapsed;
        Detail = detail;
    }

    public string VolumeRoot { get; }
    public VolumeFragmentationAnalysisBudget Budget { get; }
    public VolumeFragmentationAnalysisStatus Status { get; }
    public VolumeFragmentationEvidence? Evidence { get; }
    public uint? ProviderReturnCode { get; }
    public TimeSpan Elapsed { get; }
    public string Detail { get; }

    public static VolumeFragmentationAnalysisResult Completed(
        VolumeFragmentationAnalysisBudget budget,
        VolumeFragmentationEvidence evidence,
        TimeSpan elapsed,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new VolumeFragmentationAnalysisResult(
            evidence.VolumeRoot,
            budget,
            VolumeFragmentationAnalysisStatus.Completed,
            evidence,
            0,
            elapsed,
            detail);
    }

    public static VolumeFragmentationAnalysisResult Unavailable(
        string volumeRoot,
        VolumeFragmentationAnalysisBudget budget,
        VolumeFragmentationAnalysisStatus status,
        uint? providerReturnCode,
        TimeSpan elapsed,
        string detail)
    {
        if (status == VolumeFragmentationAnalysisStatus.Completed)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new VolumeFragmentationAnalysisResult(
            volumeRoot,
            budget,
            status,
            null,
            providerReturnCode,
            elapsed,
            detail);
    }
}

public interface IVolumeFragmentationAnalysisProvider
{
    ValueTask<VolumeFragmentationAnalysisResult> AnalyzeAsync(
        string volumeRoot,
        VolumeFragmentationAnalysisBudget budget,
        CancellationToken cancellationToken = default);
}

public static class VolumeFragmentationDriveRoot
{
    public static string RequireCanonical(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        if (volumeRoot.Length != 3 ||
            !char.IsAsciiLetter(volumeRoot[0]) ||
            volumeRoot[1] != ':' ||
            (volumeRoot[2] != '\\' && volumeRoot[2] != '/'))
        {
            throw new ArgumentException(
                "This fragmentation-analysis slice supports only explicit local drive-letter roots such as C:\\.",
                nameof(volumeRoot));
        }

        return $"{char.ToUpperInvariant(volumeRoot[0])}:\\";
    }
}
