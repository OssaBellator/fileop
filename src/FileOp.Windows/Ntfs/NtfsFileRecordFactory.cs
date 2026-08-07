using FileOp.Core.Models;

namespace FileOp.Windows.Ntfs;

public static class NtfsFileRecordFactory
{
    public static FileRecord Create(
        NtfsVolume volume,
        string path,
        NtfsMftEntry entry,
        NtfsFileMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entry);

        var fullPath = Path.GetFullPath(path);
        var isDirectory = metadata?.IsDirectory ?? entry.IsDirectory;
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            name = entry.Name;
        }

        var parentPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var identity = new FileIdentity(volume.VolumeIdentity, entry.FileReferenceNumber);
        var parentIdentity = new FileIdentity(volume.VolumeIdentity, entry.ParentFileReferenceNumber);

        return new FileRecord(
            fullPath,
            name,
            parentPath,
            isDirectory ? string.Empty : Path.GetExtension(name),
            isDirectory ? 0 : metadata?.Length ?? 0,
            isDirectory,
            metadata?.LastWriteTime ?? entry.Timestamp,
            metadata?.Attributes ?? entry.Attributes,
            identity,
            parentIdentity,
            isDirectory ? 0 : metadata?.AllocatedLength);
    }
}
