namespace FileOp.Core.Models;

public readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong FileReferenceNumber)
{
    public override string ToString() => $"{VolumeSerialNumber:X16}:{FileReferenceNumber:X16}";
}
