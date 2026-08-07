using FileOp.Core.Models;

namespace FileOp.Windows.Ntfs;

public enum NtfsIndexMutationKind
{
    Upsert,
    Delete,
    Move,
}

public sealed record NtfsIndexMutation(
    NtfsIndexMutationKind Kind,
    FileRecord? Record = null,
    FileIdentity? Identity = null,
    string? Path = null,
    string? OldPath = null,
    bool DeleteSubtree = false)
{
    public static NtfsIndexMutation Upsert(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new NtfsIndexMutation(NtfsIndexMutationKind.Upsert, Record: record);
    }

    public static NtfsIndexMutation Delete(FileIdentity identity, string path, bool deleteSubtree) =>
        new(
            NtfsIndexMutationKind.Delete,
            Identity: identity,
            Path: path,
            DeleteSubtree: deleteSubtree);

    public static NtfsIndexMutation Move(string oldPath, FileRecord newRecord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentNullException.ThrowIfNull(newRecord);
        return new NtfsIndexMutation(
            NtfsIndexMutationKind.Move,
            Record: newRecord,
            Identity: newRecord.Identity,
            Path: newRecord.Path,
            OldPath: oldPath);
    }
}
