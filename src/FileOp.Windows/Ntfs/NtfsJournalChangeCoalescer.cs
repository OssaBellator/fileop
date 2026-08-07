namespace FileOp.Windows.Ntfs;

public sealed class NtfsJournalChangeCoalescer
{
    public NtfsJournalChangeSet Coalesce(NtfsJournalBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var pendingRenameOld = new Dictionary<ulong, NtfsMftEntry>();
        var changes = new List<NtfsJournalChange>(batch.Records.Count);

        foreach (var record in batch.Records)
        {
            if ((record.Reason & UsnReason.HardLinkChange) != 0)
            {
                throw new NtfsIndexResnapshotRequiredException(
                    $"A hard-link namespace change was observed for file reference {record.FileReferenceNumber}. " +
                    "The current NTFS snapshot model does not preserve every hard-link name, so a fresh namespace snapshot is required.");
            }

            if ((record.Reason & UsnReason.RenameOldName) != 0)
            {
                pendingRenameOld[record.FileReferenceNumber] = record;
                continue;
            }

            if ((record.Reason & UsnReason.RenameNewName) != 0)
            {
                if (pendingRenameOld.Remove(record.FileReferenceNumber, out var oldRecord))
                {
                    changes.Add(new NtfsJournalChange(
                        NtfsJournalChangeKind.Rename,
                        record.FileReferenceNumber,
                        record.ParentFileReferenceNumber,
                        record.Name,
                        record.Usn,
                        record.Timestamp,
                        record.Reason,
                        record.Attributes,
                        oldRecord.ParentFileReferenceNumber,
                        oldRecord.Name));
                }
                else
                {
                    // A snapshot/checkpoint can legitimately begin between the old/new halves.
                    // Treat the new name as authoritative and let the index translator reconcile
                    // any previous namespace row for this file identity.
                    changes.Add(ToUpsert(record));
                }

                continue;
            }

            if ((record.Reason & UsnReason.FileDelete) != 0)
            {
                changes.Add(new NtfsJournalChange(
                    NtfsJournalChangeKind.Delete,
                    record.FileReferenceNumber,
                    record.ParentFileReferenceNumber,
                    record.Name,
                    record.Usn,
                    record.Timestamp,
                    record.Reason,
                    record.Attributes));
                continue;
            }

            if (HasMaterialChange(record.Reason))
            {
                changes.Add(ToUpsert(record));
            }
        }

        var durableNextUsn = batch.NextCheckpoint.NextUsn;
        if (pendingRenameOld.Count > 0)
        {
            durableNextUsn = pendingRenameOld.Values.Min(static record => record.Usn);
            changes.RemoveAll(change => change.Usn >= durableNextUsn);
        }

        return new NtfsJournalChangeSet(
            batch.PreviousCheckpoint,
            new NtfsJournalCheckpoint(batch.NextCheckpoint.JournalId, durableNextUsn),
            changes);
    }

    private static NtfsJournalChange ToUpsert(NtfsMftEntry record) =>
        new(
            NtfsJournalChangeKind.Upsert,
            record.FileReferenceNumber,
            record.ParentFileReferenceNumber,
            record.Name,
            record.Usn,
            record.Timestamp,
            record.Reason,
            record.Attributes);

    private static bool HasMaterialChange(UsnReason reason) =>
        (reason & ~UsnReason.Close) != UsnReason.None;
}
