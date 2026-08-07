using FileOp.Core.Models;

namespace FileOp.Core.Search;

public interface IFileIndex
{
    int Count { get; }

    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    ValueTask AddBatchAsync(IReadOnlyList<FileRecord> records, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<FileRecord>> SearchAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default);
}
