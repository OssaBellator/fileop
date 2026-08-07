namespace FileOp.Windows.Ntfs;

public enum NtfsJournalChangeKind
{
    Upsert,
    Delete,
    Rename,
    HardLinkRefresh,
}

public sealed record NtfsJournalChange(
    NtfsJournalChangeKind Kind,
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    string Name,
    long Usn,
    DateTimeOffset Timestamp,
    UsnReason Reason,
    FileAttributes Attributes,
    ulong? OldParentFileReferenceNumber = null,
    string? OldName = null)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

public sealed record NtfsJournalChangeSet(
    NtfsJournalCheckpoint PreviousCheckpoint,
    NtfsJournalCheckpoint DurableCheckpoint,
    IReadOnlyList<NtfsJournalChange> Changes);
