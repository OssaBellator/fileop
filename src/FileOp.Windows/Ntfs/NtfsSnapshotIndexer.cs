using FileOp.Core.Models;
using FileOp.Core.Search;

namespace FileOp.Windows.Ntfs;

public sealed class NtfsSnapshotIndexer
{
    private const int BatchSize = 1024;

    private readonly IFileIndex _index;
    private readonly IIndexCheckpointStore _checkpointStore;
    private readonly NtfsUsnJournal _journal;
    private readonly NtfsFileMetadataReader _metadataReader;

    public NtfsSnapshotIndexer(
        IFileIndex index,
        IIndexCheckpointStore checkpointStore,
        NtfsUsnJournal? journal = null,
        NtfsFileMetadataReader? metadataReader = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _journal = journal ?? new NtfsUsnJournal();
        _metadataReader = metadataReader ?? new NtfsFileMetadataReader();
    }

    public async ValueTask<NtfsJournalCheckpoint> RebuildAsync(
        NtfsVolume volume,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var checkpoint = _journal.GetCurrentCheckpoint(volume);
        var entries = _journal.EnumerateMft(volume, checkpoint, cancellationToken).ToArray();
        var paths = NtfsPathResolver.Resolve(volume.RootPath, entries);

        await _index.ClearAsync(cancellationToken).ConfigureAwait(false);

        var batch = new List<FileRecord>(BatchSize);
        var indexed = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!paths.TryGetValue(entry.FileReferenceNumber, out var path))
            {
                continue;
            }

            var metadata = _metadataReader.TryRead(volume, entry.FileReferenceNumber);
            if (metadata is null)
            {
                continue;
            }

            batch.Add(NtfsFileRecordFactory.Create(volume, path, entry, metadata));
            if (batch.Count < BatchSize)
            {
                continue;
            }

            await _index.AddBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            indexed += batch.Count;
            progress?.Report(indexed);
            batch.Clear();
        }

        if (batch.Count > 0)
        {
            await _index.AddBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            indexed += batch.Count;
            progress?.Report(indexed);
        }

        await _checkpointStore.SaveCheckpointAsync(
            new IndexSourceCheckpoint(
                NtfsIndexSynchronizer.CreateSourceKey(volume),
                checkpoint.JournalId,
                checkpoint.NextUsn,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return checkpoint;
    }
}
