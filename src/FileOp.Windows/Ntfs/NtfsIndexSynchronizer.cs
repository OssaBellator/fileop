using System.ComponentModel;
using FileOp.Core.Models;

namespace FileOp.Windows.Ntfs;

public sealed class NtfsIndexSynchronizer
{
    private readonly NtfsUsnJournal _journal;
    private readonly NtfsJournalChangeCoalescer _coalescer;
    private readonly NtfsFileMetadataReader _metadataReader;
    private readonly INtfsHardLinkEnumerator _hardLinkEnumerator;
    private readonly NtfsSqliteNamespaceStore _store;

    public NtfsIndexSynchronizer(
        NtfsSqliteNamespaceStore store,
        NtfsUsnJournal? journal = null,
        NtfsJournalChangeCoalescer? coalescer = null,
        NtfsFileMetadataReader? metadataReader = null,
        INtfsHardLinkEnumerator? hardLinkEnumerator = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _journal = journal ?? new NtfsUsnJournal();
        _coalescer = coalescer ?? new NtfsJournalChangeCoalescer();
        _metadataReader = metadataReader ?? new NtfsFileMetadataReader();
        _hardLinkEnumerator = hardLinkEnumerator ?? new NtfsHardLinkEnumerator();
    }

    public async ValueTask<NtfsJournalCheckpoint> ApplyNextBatchAsync(
        NtfsVolume volume,
        NtfsJournalCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        cancellationToken.ThrowIfCancellationRequested();

        // A synchronization checkpoint is only safe if every namespace-relevant journal
        // record before it has been observed. Do not expose a caller-supplied reason mask:
        // FSCTL_READ_USN_JOURNAL can advance the returned cursor past filtered-out records.
        var batch = _journal.ReadChanges(volume, checkpoint, uint.MaxValue, cancellationToken);
        var changeSet = _coalescer.Coalesce(batch);
        var mutations = await TranslateAsync(volume, changeSet, cancellationToken).ConfigureAwait(false);

        await _store.ApplyAsync(
            mutations,
            CreateSourceKey(volume),
            changeSet.DurableCheckpoint,
            cancellationToken).ConfigureAwait(false);

        return changeSet.DurableCheckpoint;
    }

    public static string CreateSourceKey(NtfsVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return $"ntfs:{volume.VolumeIdentity:X16}";
    }

    private async ValueTask<IReadOnlyList<NtfsIndexMutation>> TranslateAsync(
        NtfsVolume volume,
        NtfsJournalChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        var mutations = new List<NtfsIndexMutation>(changeSet.Changes.Count);
        var directoryOverlay = new Dictionary<FileIdentity, FileRecord>();
        var mutatedIdentities = new HashSet<FileIdentity>();
        var refreshedHardLinkIdentities = new HashSet<FileIdentity>();

        foreach (var change in changeSet.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var identity = Identity(volume, change.FileReferenceNumber);
            var parentIdentity = Identity(volume, change.ParentFileReferenceNumber);

            if (change.Kind == NtfsJournalChangeKind.HardLinkRefresh)
            {
                if (mutatedIdentities.Contains(identity))
                {
                    throw new NtfsIndexResnapshotRequiredException(
                        $"Multiple namespace changes for hard-linked file {identity} occurred in one journal batch. " +
                        "A fresh snapshot is required to avoid reconciling against uncommitted namespace rows.");
                }

                refreshedHardLinkIdentities.Add(identity);
            }
            else if (refreshedHardLinkIdentities.Contains(identity))
            {
                throw new NtfsIndexResnapshotRequiredException(
                    $"File {identity} changed again after a hard-link refresh in the same journal batch. " +
                    "A fresh snapshot is required to preserve namespace ordering.");
            }

            switch (change.Kind)
            {
                case NtfsJournalChangeKind.Upsert:
                    await TranslateUpsertAsync(
                        volume,
                        change,
                        identity,
                        parentIdentity,
                        directoryOverlay,
                        mutations,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case NtfsJournalChangeKind.Delete:
                    await TranslateDeleteAsync(
                        change,
                        identity,
                        parentIdentity,
                        directoryOverlay,
                        mutations,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case NtfsJournalChangeKind.Rename:
                    await TranslateRenameAsync(
                        volume,
                        change,
                        identity,
                        parentIdentity,
                        directoryOverlay,
                        mutations,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case NtfsJournalChangeKind.HardLinkRefresh:
                    await TranslateHardLinkRefreshAsync(
                        volume,
                        change,
                        identity,
                        parentIdentity,
                        directoryOverlay,
                        mutations,
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(changeSet), change.Kind, "Unknown NTFS journal change kind.");
            }

            mutatedIdentities.Add(identity);
        }

        return mutations;
    }

    private async ValueTask TranslateUpsertAsync(
        NtfsVolume volume,
        NtfsJournalChange change,
        FileIdentity identity,
        FileIdentity parentIdentity,
        Dictionary<FileIdentity, FileRecord> directoryOverlay,
        List<NtfsIndexMutation> mutations,
        CancellationToken cancellationToken)
    {
        var path = await ResolvePathAsync(
            volume,
            identity,
            parentIdentity,
            change.Name,
            directoryOverlay,
            cancellationToken).ConfigureAwait(false);

        var metadata = _metadataReader.TryRead(volume, change.FileReferenceNumber);
        if (metadata is null)
        {
            await DeleteKnownIdentityRowsAsync(
                identity,
                directoryOverlay,
                mutations,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var record = NtfsFileRecordFactory.Create(volume, path, ToEntry(change), metadata);
        var existingRows = await _store.FindByIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        var exactPath = existingRows.FirstOrDefault(
            existing => string.Equals(existing.Path, record.Path, StringComparison.OrdinalIgnoreCase));

        var isRenameNewWithoutOld = (change.Reason & UsnReason.RenameNewName) != 0;
        if (isRenameNewWithoutOld && exactPath is null && existingRows.Count == 1)
        {
            mutations.Add(NtfsIndexMutation.Move(existingRows[0].Path, record));
        }
        else if (isRenameNewWithoutOld && exactPath is null && existingRows.Count > 1)
        {
            throw new NtfsIndexResnapshotRequiredException(
                $"A rename for {identity} began before the readable journal window and the file has multiple namespace rows. A fresh namespace snapshot is required.");
        }
        else if (!isRenameNewWithoutOld && existingRows.Count > 1)
        {
            if (exactPath is null)
            {
                throw new NtfsIndexResnapshotRequiredException(
                    $"A metadata change for hard-linked file {identity} referenced unknown path {record.Path}. " +
                    "A fresh namespace snapshot is required.");
            }

            foreach (var existing in existingRows)
            {
                mutations.Add(NtfsIndexMutation.Upsert(existing with
                {
                    Length = metadata.IsDirectory ? 0 : metadata.Length,
                    IsDirectory = metadata.IsDirectory,
                    LastWriteTime = metadata.LastWriteTime,
                    Attributes = metadata.Attributes,
                    AllocatedLength = metadata.IsDirectory ? 0 : metadata.AllocatedLength,
                }));
            }
        }
        else
        {
            mutations.Add(NtfsIndexMutation.Upsert(record));
        }

        TrackDirectory(directoryOverlay, record);
    }

    private async ValueTask TranslateDeleteAsync(
        NtfsJournalChange change,
        FileIdentity identity,
        FileIdentity parentIdentity,
        Dictionary<FileIdentity, FileRecord> directoryOverlay,
        List<NtfsIndexMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (directoryOverlay.TryGetValue(identity, out var overlayRecord) &&
            overlayRecord.ParentIdentity == parentIdentity &&
            string.Equals(overlayRecord.Name, change.Name, StringComparison.OrdinalIgnoreCase))
        {
            mutations.Add(NtfsIndexMutation.Delete(identity, overlayRecord.Path, overlayRecord.IsDirectory));
            directoryOverlay.Remove(identity);
            return;
        }

        var rows = await _store.FindByParentAndNameAsync(parentIdentity, change.Name, cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows.Where(row => row.Identity == identity))
        {
            mutations.Add(NtfsIndexMutation.Delete(identity, row.Path, row.IsDirectory));
        }

        directoryOverlay.Remove(identity);
    }

    private async ValueTask TranslateRenameAsync(
        NtfsVolume volume,
        NtfsJournalChange change,
        FileIdentity identity,
        FileIdentity newParentIdentity,
        Dictionary<FileIdentity, FileRecord> directoryOverlay,
        List<NtfsIndexMutation> mutations,
        CancellationToken cancellationToken)
    {
        if (change.OldParentFileReferenceNumber is not { } oldParentReference ||
            string.IsNullOrEmpty(change.OldName))
        {
            throw new InvalidDataException("A normalized rename change is missing its old namespace information.");
        }

        var oldParentIdentity = Identity(volume, oldParentReference);
        var oldParentPath = await ResolveParentPathAsync(
            volume,
            oldParentIdentity,
            directoryOverlay,
            cancellationToken).ConfigureAwait(false);
        var newParentPath = await ResolveParentPathAsync(
            volume,
            newParentIdentity,
            directoryOverlay,
            cancellationToken).ConfigureAwait(false);

        var oldPath = Path.Combine(oldParentPath, change.OldName);
        var newPath = Path.Combine(newParentPath, change.Name);
        var metadata = _metadataReader.TryRead(volume, change.FileReferenceNumber);

        if (metadata is null)
        {
            // OpenFileById can no longer resolve this full file identity, so the file no
            // longer exists. Remove every known namespace row for the identity; deleting
            // only the journal's old path can leave a stale row after a later rename/delete
            // races ahead of hydration.
            await DeleteKnownIdentityRowsAsync(
                identity,
                directoryOverlay,
                mutations,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var record = NtfsFileRecordFactory.Create(volume, newPath, ToEntry(change), metadata);
        mutations.Add(NtfsIndexMutation.Move(oldPath, record));
        TrackDirectory(directoryOverlay, record);
    }

    private async ValueTask TranslateHardLinkRefreshAsync(
        NtfsVolume volume,
        NtfsJournalChange change,
        FileIdentity identity,
        FileIdentity eventParentIdentity,
        Dictionary<FileIdentity, FileRecord> directoryOverlay,
        List<NtfsIndexMutation> mutations,
        CancellationToken cancellationToken)
    {
        var metadata = _metadataReader.TryRead(volume, change.FileReferenceNumber);
        if (metadata is null)
        {
            await DeleteKnownIdentityRowsAsync(
                identity,
                directoryOverlay,
                mutations,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var existingRows = await _store.FindByIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string>? currentLinkPaths = null;

        foreach (var row in existingRows)
        {
            try
            {
                currentLinkPaths = _hardLinkEnumerator.Enumerate(row.Path);
                break;
            }
            catch (Win32Exception exception) when (IsMissingPathError(exception.NativeErrorCode))
            {
                // A removed hard-link path can remain in the persisted snapshot until this
                // journal record is committed. Try another known alias before failing safe.
            }
        }

        var eventPath = await ResolvePathAsync(
            volume,
            identity,
            eventParentIdentity,
            change.Name,
            directoryOverlay,
            cancellationToken).ConfigureAwait(false);

        if (currentLinkPaths is null)
        {
            try
            {
                currentLinkPaths = _hardLinkEnumerator.Enumerate(eventPath);
            }
            catch (Win32Exception exception) when (IsMissingPathError(exception.NativeErrorCode))
            {
                throw new NtfsIndexResnapshotRequiredException(
                    $"Could not find a live namespace path for hard-linked file {identity}. A fresh snapshot is required.",
                    exception);
            }
        }

        mutations.AddRange(NtfsHardLinkNamespacePlanner.PlanRefresh(
            volume,
            change,
            identity,
            metadata,
            existingRows,
            eventPath,
            eventParentIdentity,
            currentLinkPaths));
    }

    private async ValueTask DeleteKnownIdentityRowsAsync(
        FileIdentity identity,
        Dictionary<FileIdentity, FileRecord> directoryOverlay,
        List<NtfsIndexMutation> mutations,
        CancellationToken cancellationToken)
    {
        string? overlayPath = null;
        if (directoryOverlay.Remove(identity, out var overlay))
        {
            overlayPath = overlay.Path;
            mutations.Add(NtfsIndexMutation.Delete(identity, overlay.Path, overlay.IsDirectory));
        }

        var rows = await _store.FindByIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            if (overlayPath is not null &&
                string.Equals(row.Path, overlayPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            mutations.Add(NtfsIndexMutation.Delete(identity, row.Path, row.IsDirectory));
        }
    }

    private async ValueTask<string> ResolvePathAsync(
        NtfsVolume volume,
        FileIdentity identity,
        FileIdentity parentIdentity,
        string name,
        Dictionary<FileIdentity, FileRecord> directoryOverlay,
        CancellationToken cancellationToken)
    {
        if (identity == parentIdentity)
        {
            return Path.GetFullPath(volume.RootPath);
        }

        var parentPath = await ResolveParentPathAsync(
            volume,
            parentIdentity,
            directoryOverlay,
            cancellationToken).ConfigureAwait(false);
        return Path.Combine(parentPath, name);
    }

    private async ValueTask<string> ResolveParentPathAsync(
        NtfsVolume volume,
        FileIdentity parentIdentity,
        Dictionary<FileIdentity, FileRecord> directoryOverlay,
        CancellationToken cancellationToken)
    {
        if (directoryOverlay.TryGetValue(parentIdentity, out var overlay) && overlay.IsDirectory)
        {
            return overlay.Path;
        }

        var rows = await _store.FindByIdentityAsync(parentIdentity, cancellationToken).ConfigureAwait(false);
        var directory = rows.FirstOrDefault(static row => row.IsDirectory);
        if (directory is not null)
        {
            return directory.Path;
        }

        throw new NtfsIndexResnapshotRequiredException(
            $"Could not resolve parent {parentIdentity} while applying changes on {volume.RootPath}. A fresh namespace snapshot is required.");
    }

    private static NtfsMftEntry ToEntry(NtfsJournalChange change) =>
        new(
            change.FileReferenceNumber,
            change.ParentFileReferenceNumber,
            change.Usn,
            change.Timestamp,
            change.Reason,
            change.Attributes,
            change.Name);

    private static FileIdentity Identity(NtfsVolume volume, ulong fileReferenceNumber) =>
        new(volume.VolumeIdentity, fileReferenceNumber);

    private static bool IsMissingPathError(int error) => error is 2 or 3 or 1168;

    private static void TrackDirectory(
        Dictionary<FileIdentity, FileRecord> directoryOverlay,
        FileRecord record)
    {
        if (record.IsDirectory && record.Identity is { } identity)
        {
            directoryOverlay[identity] = record;
        }
    }
}
