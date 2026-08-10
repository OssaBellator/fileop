namespace FileOp.Core.Storage;

public interface IStorageOptimizationAnalytics
{
    ValueTask<StorageOptimizationAnalysis> AnalyzeOptimizationAsync(
        string rootPath,
        StorageOptimizationPolicy? policy = null,
        DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default);
}

public sealed record StorageOptimizationPolicy(
    long LargeFileMinimumBytes = 512L * 1024 * 1024,
    long SameSizeMinimumBytes = 64L * 1024 * 1024,
    int StaleAgeDays = 180,
    int MaxLargeFiles = 50,
    int MaxStaleLargeFiles = 50,
    int MaxSameSizeGroups = 25,
    int MaxFilesPerSameSizeGroup = 8)
{
    public static StorageOptimizationPolicy Default { get; } = new();
}

public sealed record StorageOptimizationFileCandidate(
    string Path,
    string Name,
    string Extension,
    StorageFileCategory Category,
    long LogicalBytes,
    long? AllocatedBytes,
    DateTimeOffset LastWriteTime)
{
    public long MeasuredBytes => AllocatedBytes ?? LogicalBytes;
}

public sealed record StorageSameSizeCandidateFile(
    string Path,
    string Name,
    string Extension,
    StorageFileCategory Category,
    long LogicalBytes,
    long? AllocatedBytes,
    DateTimeOffset LastWriteTime);

public sealed record StorageSameSizeCandidateGroup(
    long LogicalBytesPerFile,
    int CandidateFileCount,
    long PotentialLogicalSavingsUpperBound,
    IReadOnlyList<StorageSameSizeCandidateFile> SampleFiles)
{
    public int AdditionalCopies => Math.Max(0, CandidateFileCount - 1);
}

public sealed record StorageOptimizationAnalysis(
    string RootPath,
    DateTimeOffset AsOfUtc,
    StorageOptimizationPolicy Policy,
    IReadOnlyList<StorageOptimizationFileCandidate> LargestFiles,
    IReadOnlyList<StorageOptimizationFileCandidate> StaleLargeFiles,
    IReadOnlyList<StorageSameSizeCandidateGroup> SameSizeCandidateGroups)
{
    public long SameSizePotentialLogicalSavingsUpperBound =>
        SameSizeCandidateGroups.Sum(static group => group.PotentialLogicalSavingsUpperBound);
}
