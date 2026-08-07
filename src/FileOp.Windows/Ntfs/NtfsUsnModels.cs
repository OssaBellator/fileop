namespace FileOp.Windows.Ntfs;

[Flags]
public enum UsnReason : uint
{
    None = 0,
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    ExtendedAttributeChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    IntegrityChange = 0x00800000,
    Close = 0x80000000,
}

public sealed record NtfsJournalState(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    long MaxUsn,
    ulong MaximumSize,
    ulong AllocationDelta);

public sealed record NtfsMftEntry(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    long Usn,
    DateTimeOffset Timestamp,
    UsnReason Reason,
    FileAttributes Attributes,
    string Name)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

public readonly record struct NtfsJournalCheckpoint(ulong JournalId, long NextUsn);

public sealed record NtfsJournalBatch(
    NtfsJournalCheckpoint PreviousCheckpoint,
    NtfsJournalCheckpoint NextCheckpoint,
    IReadOnlyList<NtfsMftEntry> Records);
