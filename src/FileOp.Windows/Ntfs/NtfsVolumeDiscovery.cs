using System.Runtime.InteropServices;
using System.Text;

namespace FileOp.Windows.Ntfs;

public static class NtfsVolumeDiscovery
{
    public static IReadOnlyList<NtfsVolume> GetVolumes()
    {
        var result = new List<NtfsVolume>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rootPath = drive.RootDirectory.FullName;
                if (rootPath.Length < 2 || rootPath[1] != ':')
                {
                    continue;
                }

                if (!TryGetSerialNumber(rootPath, out var serialNumber))
                {
                    continue;
                }

                TryGetVolumeGuidPath(rootPath, out var volumeGuidPath);
                result.Add(new NtfsVolume(
                    rootPath,
                    $@"\\.\{rootPath[..2]}",
                    drive.VolumeLabel,
                    serialNumber,
                    volumeGuidPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private static bool TryGetSerialNumber(string rootPath, out uint serialNumber)
    {
        var volumeName = new StringBuilder(261);
        var fileSystemName = new StringBuilder(261);

        return GetVolumeInformationW(
            rootPath,
            volumeName,
            volumeName.Capacity,
            out serialNumber,
            out _,
            out _,
            fileSystemName,
            fileSystemName.Capacity);
    }

    private static bool TryGetVolumeGuidPath(string rootPath, out string? volumeGuidPath)
    {
        var buffer = new StringBuilder(64);
        if (GetVolumeNameForVolumeMountPointW(rootPath, buffer, buffer.Capacity))
        {
            volumeGuidPath = buffer.ToString();
            return true;
        }

        volumeGuidPath = null;
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        int cchBufferLength);
}
