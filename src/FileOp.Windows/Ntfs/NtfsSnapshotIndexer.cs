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

        var sourceKey = NtfsIndexSynchronizer.CreateSourceKey(volume);
        var checkpoint = _journal.GetCurrentCheckpoint(volume);
        var entries = _journal.EnumerateMft(volume, checkpoint, cancellationToken).ToArray();
        var paths = NtfsPathResolver.Resolve(volume.RootPath, entries);

        // Invalidate the durable cursor before touching index contents. This is redundant
        // when both interfaces are backed by SqliteFileIndex (ClearAsync also clears its
        // checkpoints), but it keeps interrupted rebuilds safe when callers provide a
        // separate IIndexCheckpointStore.
        await _checkpointStore.DeleteCheckpointAsync(sourceKey, cancellationToken).ConfigureAwait(false);
        await _index.ClearAsync(cancellationToken).ConfigureAwait(false);

        var indexed = 0;
        for (var offset = 0; offset < entries.Length; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(BatchSize, entries.Length - offset);
            var entryBatch = new ArraySegment<NtfsMftEntry>(entries, offset, count);
            var metadataByFileReference = _metadataReader.ReadBatch(
                volume,
                entryBatch.Select(static entry => entry.FileReferenceNumber),
                cancellationToken);
            var records = new List<FileRecord>(count);

            foreach (var entry in entryBatch)
            {
                if (!paths.TryGetValue(entry.FileReferenceNumber, out var path) ||
                    !metadataByFileReference.TryGetValue(entry.FileReferenceNumber, out var metadata))
                {
                    continue;
                }

                records.Add(NtfsFileRecordFactory.Create(volume, path, entry, metadata));
            }

            if (records.Count == 0)
            {
                continue;
            }

            await _index.AddBatchAsync(records, cancellationToken).ConfigureAwait(false);
            indexed += records.Count;
            progress?.Report(indexed);
        }

        await _checkpointStore.SaveCheckpointAsync(
            new IndexSourceCheckpoint(
                sourceKey,
                checkpoint.JournalId,
                checkpoint.NextUsn,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return checkpoint;
    }
}
