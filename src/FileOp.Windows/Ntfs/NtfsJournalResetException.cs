namespace FileOp.Windows.Ntfs;

public sealed class NtfsJournalResetException : IOException
{
    public NtfsJournalResetException(string message)
        : base(message)
    {
    }
}
