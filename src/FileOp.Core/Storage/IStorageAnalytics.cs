namespace FileOp.Core.Storage;

public interface IStorageAnalytics
{
    ValueTask<StorageDirectoryAnalysis> AnalyzeDirectoryAsync(
        string rootPath,
        int maxEntries = 256,
        CancellationToken cancellationToken = default);

    ValueTask<StorageFileTypeAnalysis> AnalyzeFileTypesAsync(
        string rootPath,
        int maxTypes = 128,
        CancellationToken cancellationToken = default);
}

public enum StorageFileCategory
{
    NoExtension,
    Documents,
    Images,
    Video,
    Audio,
    Archives,
    Applications,
    Code,
    Data,
    DiskImages,
    Fonts,
    Other,
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

public sealed record StorageFileTypeEntry(
    string Extension,
    StorageFileCategory Category,
    long LogicalBytes,
    long? AllocatedBytes,
    int FileCount,
    int HardLinkAliasCount)
{
    public int UniqueFileCount => FileCount - HardLinkAliasCount;

    public long TreemapBytes => AllocatedBytes ?? LogicalBytes;
}

public sealed record StorageFileTypeAnalysis(
    string RootPath,
    long LogicalBytes,
    long? AllocatedBytes,
    int FileCount,
    int HardLinkAliasCount,
    int TypeCount,
    IReadOnlyList<StorageFileTypeEntry> Types)
{
    public int UniqueFileCount => FileCount - HardLinkAliasCount;
}
