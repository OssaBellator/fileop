namespace FileOp.Windows.Ntfs;

public sealed record NtfsFileMetadata(
    long Length,
    long AllocatedLength,
    uint NumberOfLinks,
    bool IsDirectory,
    DateTimeOffset LastWriteTime,
    FileAttributes Attributes);
