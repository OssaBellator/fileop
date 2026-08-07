using FileOp.Core.Models;

namespace FileOp.Core.Search;

public enum FileIndexChangeKind
{
    Upsert,
    Delete,
}

public sealed record FileIndexChange(
    FileIndexChangeKind Kind,
    FileRecord? Record,
    FileIdentity? Identity,
    string? Path)
{
    public static FileIndexChange Upsert(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new FileIndexChange(FileIndexChangeKind.Upsert, record, record.Identity, record.Path);
    }

    public static FileIndexChange Delete(FileIdentity identity, string? path = null) =>
        new(FileIndexChangeKind.Delete, null, identity, path);
}
