namespace FileOp.Windows.Ntfs;

public sealed record NtfsVolume(
    string RootPath,
    string DevicePath,
    string Label,
    uint SerialNumber,
    string? VolumeGuidPath = null)
{
    public ulong VolumeIdentity => CreateVolumeIdentity(VolumeGuidPath, SerialNumber);

    private static ulong CreateVolumeIdentity(string? volumeGuidPath, uint serialNumber)
    {
        if (string.IsNullOrWhiteSpace(volumeGuidPath))
        {
            return serialNumber;
        }

        var openBrace = volumeGuidPath.IndexOf('{');
        var closeBrace = volumeGuidPath.IndexOf('}', openBrace + 1);
        if (openBrace < 0 || closeBrace <= openBrace ||
            !Guid.TryParse(volumeGuidPath.AsSpan(openBrace, closeBrace - openBrace + 1), out var volumeGuid))
        {
            return serialNumber;
        }

        Span<byte> guidBytes = stackalloc byte[16];
        if (!volumeGuid.TryWriteBytes(guidBytes))
        {
            return serialNumber;
        }

        // FileIdentity stores a 64-bit provider volume token. NTFS volume serial numbers
        // are only 32-bit and can collide across disks, so hash the Windows volume GUID
        // into that token instead. FNV-1a is deterministic across processes and runtimes.
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var value in guidBytes)
        {
            hash ^= value;
            hash *= prime;
        }

        return hash;
    }
}
