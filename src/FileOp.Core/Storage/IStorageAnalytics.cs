namespace FileOp.Core.Storage;

public interface IStorageAnalytics
{
    ValueTask<StorageDirectoryAnalysis> AnalyzeDirectoryAsync(
        string rootPath,
        int maxEntries = 256,
        CancellationToken cancellationToken = default);
}

public sealed record StorageDirectoryEntry(
    string Path,
    string Name,
    bool IsDirectory,
    long LogicalBytes,
    long? AllocatedBytes,
    int FileCount,
    int DirectoryCount,
    int HardLinkAliasCount)
{
    public int UniqueFileCount => FileCount - HardLinkAliasCount;

    public long TreemapBytes => AllocatedBytes ?? LogicalBytes;
}

public sealed record StorageDirectoryAnalysis(
    string RootPath,
    long LogicalBytes,
    long? AllocatedBytes,
    int FileCount,
    int DirectoryCount,
    int HardLinkAliasCount,
    int DirectEntryCount,
    IReadOnlyList<StorageDirectoryEntry> Entries)
{
    public int UniqueFileCount => FileCount - HardLinkAliasCount;
}
