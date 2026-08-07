namespace FileOp.Core.Search;

public interface IIndexCheckpointStore
{
    ValueTask<IndexSourceCheckpoint?> GetCheckpointAsync(
        string sourceKey,
        CancellationToken cancellationToken = default);

    ValueTask SaveCheckpointAsync(
        IndexSourceCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    ValueTask DeleteCheckpointAsync(
        string sourceKey,
        CancellationToken cancellationToken = default);
}
