namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    internal static (long? TotalBytes, long? FreeBytes) ReadVolumeCapacity(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return (null, null);
        }

        try
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(rootPath));
            if (string.IsNullOrWhiteSpace(volumeRoot))
            {
                return (null, null);
            }

            var drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady)
            {
                return (null, null);
            }

            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
