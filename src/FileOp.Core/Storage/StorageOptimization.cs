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

public sealed record StorageSameSizeContentVerificationPolicy(
    int MaxFiles = 8,
    long MaxTotalBytesRead = 2L * 1024 * 1024 * 1024,
    int BufferSizeBytes = 1024 * 1024)
{
    public const int HardMaximumFiles = 8;
    public const long HardMaximumTotalBytesRead = 2L * 1024 * 1024 * 1024;
    public const int MinimumBufferSizeBytes = 64 * 1024;
    public const int MaximumBufferSizeBytes = 4 * 1024 * 1024;

    public static StorageSameSizeContentVerificationPolicy Default { get; } = new();

    public void Validate()
    {
        if (MaxFiles < 2 || MaxFiles > HardMaximumFiles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxFiles),
                MaxFiles,
                $"Content verification must select between 2 and {HardMaximumFiles:N0} files.");
        }
        if (MaxTotalBytesRead <= 0 || MaxTotalBytesRead > HardMaximumTotalBytesRead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxTotalBytesRead),
                MaxTotalBytesRead,
                $"Content verification read budget must be between 1 byte and {HardMaximumTotalBytesRead:N0} bytes.");
        }
        if (BufferSizeBytes < MinimumBufferSizeBytes || BufferSizeBytes > MaximumBufferSizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BufferSizeBytes),
                BufferSizeBytes,
                $"Content verification buffer must be between {MinimumBufferSizeBytes:N0} and {MaximumBufferSizeBytes:N0} bytes.");
        }
    }
}

public enum StorageSameSizeContentVerificationStatus
{
    Completed,
    BudgetLimited,
    CandidateChanged,
    Unavailable,
}

public sealed record StorageVerifiedContentMatchSet(IReadOnlyList<string> Paths)
{
    public int FileCount => Paths.Count;
}

public sealed record StorageSameSizeContentVerification(
    StorageSameSizeContentVerificationStatus Status,
    DateTimeOffset VerifiedAt,
    long LogicalBytesPerFile,
    int CandidateFileCount,
    int SampleFileCount,
    int SelectedFileCount,
    int FullyHashedFileCount,
    long BytesRead,
    long MaxBytesRead,
    IReadOnlyList<StorageVerifiedContentMatchSet> MatchingSets,
    string Detail)
{
    public bool HasVerifiedDuplicateEvidence =>
        Status == StorageSameSizeContentVerificationStatus.Completed &&
        MatchingSets.Any(static set => set.FileCount >= 2);

    public long VerifiedLogicalDuplicateBytes
    {
        get
        {
            if (Status != StorageSameSizeContentVerificationStatus.Completed || LogicalBytesPerFile <= 0)
            {
                return 0;
            }

            long total = 0;
            foreach (var set in MatchingSets)
            {
                var additionalCopies = Math.Max(0, set.FileCount - 1);
                if (additionalCopies == 0)
                {
                    continue;
                }

                var setBytes = LogicalBytesPerFile > long.MaxValue / additionalCopies
                    ? long.MaxValue
                    : LogicalBytesPerFile * additionalCopies;
                total = total > long.MaxValue - setBytes
                    ? long.MaxValue
                    : total + setBytes;
            }

            return total;
        }
    }
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
