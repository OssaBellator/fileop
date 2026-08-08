namespace FileOp.Core.Models;

public sealed record FileDirectoryBrowseCursor(
    bool IsDirectory,
    string Name,
    string Path);

public sealed record FileDirectoryBrowsePage(
    string DirectoryPath,
    int TotalCount,
    IReadOnlyList<FileRecord> Entries,
    FileDirectoryBrowseCursor? NextCursor);
