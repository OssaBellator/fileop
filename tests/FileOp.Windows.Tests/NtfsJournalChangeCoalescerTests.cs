using FileOp.Windows.Ntfs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class NtfsJournalChangeCoalescerTests
{
    [TestMethod]
    public void Coalesce_PairsRenameRecordsAndAdvancesCheckpoint()
    {
        var batch = new NtfsJournalBatch(
            new NtfsJournalCheckpoint(7, 100),
            new NtfsJournalCheckpoint(7, 200),
            [
                Entry(11, 2, 120, UsnReason.RenameOldName, "old.txt"),
                Entry(11, 3, 121, UsnReason.RenameNewName, "new.txt"),
            ]);

        var result = new NtfsJournalChangeCoalescer().Coalesce(batch);

        Assert.AreEqual(200L, result.DurableCheckpoint.NextUsn);
        Assert.AreEqual(1, result.Changes.Count);
        var change = result.Changes[0];
        Assert.AreEqual(NtfsJournalChangeKind.Rename, change.Kind);
        Assert.AreEqual("old.txt", change.OldName);
        Assert.AreEqual(2UL, change.OldParentFileReferenceNumber);
        Assert.AreEqual("new.txt", change.Name);
        Assert.AreEqual(3UL, change.ParentFileReferenceNumber);
    }

    [TestMethod]
    public void Coalesce_UnmatchedRenameOldPinsCheckpointAndDropsLaterChanges()
    {
        var batch = new NtfsJournalBatch(
            new NtfsJournalCheckpoint(9, 100),
            new NtfsJournalCheckpoint(9, 220),
            [
                Entry(11, 2, 150, UsnReason.RenameOldName, "old.txt"),
                Entry(12, 2, 160, UsnReason.DataExtend, "other.bin"),
            ]);

        var result = new NtfsJournalChangeCoalescer().Coalesce(batch);

        Assert.AreEqual(150L, result.DurableCheckpoint.NextUsn);
        Assert.AreEqual(0, result.Changes.Count);
    }

    [TestMethod]
    public void Coalesce_RenameNewWithoutOldBecomesAuthoritativeUpsert()
    {
        var batch = new NtfsJournalBatch(
            new NtfsJournalCheckpoint(3, 100),
            new NtfsJournalCheckpoint(3, 140),
            [Entry(44, 5, 130, UsnReason.RenameNewName, "renamed.dat")]);

        var result = new NtfsJournalChangeCoalescer().Coalesce(batch);

        Assert.AreEqual(1, result.Changes.Count);
        Assert.AreEqual(NtfsJournalChangeKind.Upsert, result.Changes[0].Kind);
        Assert.AreEqual("renamed.dat", result.Changes[0].Name);
        Assert.AreEqual(140L, result.DurableCheckpoint.NextUsn);
    }

    [TestMethod]
    public void Coalesce_IgnoresCloseOnlyRecords()
    {
        var batch = new NtfsJournalBatch(
            new NtfsJournalCheckpoint(5, 100),
            new NtfsJournalCheckpoint(5, 110),
            [Entry(1, 1, 105, UsnReason.Close, "ignored")]);

        var result = new NtfsJournalChangeCoalescer().Coalesce(batch);

        Assert.AreEqual(0, result.Changes.Count);
        Assert.AreEqual(110L, result.DurableCheckpoint.NextUsn);
    }

    private static NtfsMftEntry Entry(
        ulong fileReference,
        ulong parentReference,
        long usn,
        UsnReason reason,
        string name) =>
        new(
            fileReference,
            parentReference,
            usn,
            DateTimeOffset.UnixEpoch,
            reason,
            FileAttributes.Normal,
            name);
}
