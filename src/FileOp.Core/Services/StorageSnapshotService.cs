using FileOp.Core.Models;

namespace FileOp.Core.Services;

public sealed class StorageSnapshotService
{
    public IReadOnlyList<StorageVolumeSnapshot> GetReadyVolumes()
    {
        var result = new List<StorageVolumeSnapshot>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                result.Add(new StorageVolumeSnapshot(
                    drive.Name,
                    drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.DriveType,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Drives can be removed or become unavailable while the snapshot is being built.
            }
        }

        return result;
    }
}
