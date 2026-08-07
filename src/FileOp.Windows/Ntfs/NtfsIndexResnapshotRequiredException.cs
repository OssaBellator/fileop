namespace FileOp.Windows.Ntfs;

public sealed class NtfsIndexResnapshotRequiredException : IOException
{
    public NtfsIndexResnapshotRequiredException(string message)
        : base(message)
    {
    }
}
