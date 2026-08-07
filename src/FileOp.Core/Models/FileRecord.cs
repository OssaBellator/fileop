namespace FileOp.Core.Models;

public sealed record FileRecord(
    string Path,
    string Name,
    string ParentPath,
    string Extension,
    long Length,
    bool IsDirectory,
    DateTimeOffset LastWriteTime,
    FileAttributes Attributes)
{
    public static FileRecord FromFileSystemInfo(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var isDirectory = info is DirectoryInfo;
        var length = 0L;

        if (info is FileInfo file)
        {
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                // The entry may disappear or become unavailable between enumeration and metadata reads.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        DateTimeOffset lastWriteTime;
        FileAttributes attributes;

        try
        {
            lastWriteTime = info.LastWriteTimeUtc;
            attributes = info.Attributes;
        }
        catch (IOException)
        {
            lastWriteTime = DateTimeOffset.MinValue;
            attributes = 0;
        }
        catch (UnauthorizedAccessException)
        {
            lastWriteTime = DateTimeOffset.MinValue;
            attributes = 0;
        }

        return new FileRecord(
            info.FullName,
            info.Name,
            info.DirectoryNameOrEmpty(),
            isDirectory ? string.Empty : info.Extension,
            length,
            isDirectory,
            lastWriteTime,
            attributes);
    }
}

internal static class FileSystemInfoExtensions
{
    public static string DirectoryNameOrEmpty(this FileSystemInfo info) => info switch
    {
        FileInfo file => file.DirectoryName ?? string.Empty,
        DirectoryInfo directory => directory.Parent?.FullName ?? string.Empty,
        _ => string.Empty,
    };
}
