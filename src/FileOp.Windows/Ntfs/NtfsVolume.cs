namespace FileOp.Windows.Ntfs;

public sealed record NtfsVolume(
    string RootPath,
    string DevicePath,
    string Label,
    uint SerialNumber)
{
    public ulong VolumeIdentity => SerialNumber;
}
