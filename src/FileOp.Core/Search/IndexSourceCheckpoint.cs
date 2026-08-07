namespace FileOp.Core.Search;

public sealed record IndexSourceCheckpoint(
    string SourceKey,
    ulong Generation,
    long Position,
    DateTimeOffset UpdatedAt);
