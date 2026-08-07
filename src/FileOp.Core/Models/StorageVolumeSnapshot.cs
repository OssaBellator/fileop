namespace FileOp.Core.Models;

public sealed record StorageVolumeSnapshot(
    string Name,
    string Label,
    string DriveFormat,
    DriveType DriveType,
    long TotalBytes,
    long AvailableBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);

    public double UsedFraction => TotalBytes == 0 ? 0 : UsedBytes / (double)TotalBytes;
}
