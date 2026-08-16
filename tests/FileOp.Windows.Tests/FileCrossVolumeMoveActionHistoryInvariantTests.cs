using System;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveActionHistoryInvariantTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly FileOperationEntry Entry =
        new(@"C:\Source\a.txt", "a.txt", IsDirectory: false);
    private static readonly FileOperationFailure Failure =
        new("TestFailure", "test failure", @"C:\Source\a.txt", Retryable: false);
    private static readonly FileContentFingerprint Fingerprint =
        new(
            FileContentFingerprintAlgorithm.Sha256,
            new string('a', FileContentFingerprint.Sha256HexLength));

    [TestMethod]
    public void FailedStateCannotHideUnresolvedCopyMutationBarrier()
    {
        var invalid = NewEntry(
            state: FileCrossVolumeMoveEntryState.Failed,
            copyStarted: T0.AddSeconds(1),
            completed: T0.AddSeconds(2),
            failure: Failure);

        Assert.ThrowsExactly<ArgumentException>(() => NewHistory(invalid));
    }

    [TestMethod]
    public void SourceDeleteRecoveryRequiresEarlierCommittedDestinationChronology()
    {
        var invalid = NewEntry(
            state: FileCrossVolumeMoveEntryState.RecoveryRequired,
            copyStarted: T0.AddSeconds(1),
            sourceDeleteStarted: T0.AddSeconds(3),
            completed: T0.AddSeconds(4),
            failure: Failure);

        Assert.ThrowsExactly<ArgumentException>(() => NewHistory(invalid));
    }

    [TestMethod]
    public void SourceIdentityMustRemainBoundToDurableSourceRootVolume()
    {
        var invalid = NewEntry(
            state: FileCrossVolumeMoveEntryState.Pending,
            sourceIdentity: new FileIdentity(9, 100));

        Assert.ThrowsExactly<ArgumentException>(() => NewHistory(invalid));
    }

    [TestMethod]
    public void DestinationEvidenceMustRemainBoundToDurableDestinationRootVolume()
    {
        var invalid = NewEntry(
            state: FileCrossVolumeMoveEntryState.DestinationCommitted,
            destinationIdentity: new FileIdentity(9, 500),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2));

        Assert.ThrowsExactly<ArgumentException>(() => NewHistory(invalid));
    }

    [TestMethod]
    public void CopyBarrierRecoveryMayCaptureObservedDestinationWithoutProvingSafeCommit()
    {
        var valid = NewEntry(
            state: FileCrossVolumeMoveEntryState.RecoveryRequired,
            destinationIdentity: new FileIdentity(2, 500),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2),
            completed: T0.AddSeconds(2),
            failure: Failure);

        var history = NewHistory(
            valid,
            FileCrossVolumeMoveTerminalState.RecoveryRequired,
            T0.AddSeconds(3));

        Assert.IsTrue(history.RequiresRecovery);
        Assert.IsTrue(history.Entries[0].HasDestinationRecoveryEvidence);
        Assert.IsFalse(history.Entries[0].DestinationIsDurablyCommitted);
        Assert.IsFalse(history.Entries[0].SourceDeleteBarrierMayBeUnresolved);
        Assert.IsFalse(history.HasRetainedSourceDuplicates);
    }

    [TestMethod]
    public void SourceDeleteRecoveryStillProvesEarlierSafeDestinationCommit()
    {
        var valid = NewEntry(
            state: FileCrossVolumeMoveEntryState.RecoveryRequired,
            destinationIdentity: new FileIdentity(2, 500),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2),
            sourceDeleteStarted: T0.AddSeconds(3),
            completed: T0.AddSeconds(4),
            failure: Failure);

        var history = NewHistory(
            valid,
            FileCrossVolumeMoveTerminalState.RecoveryRequired,
            T0.AddSeconds(5));

        Assert.IsTrue(history.RequiresRecovery);
        Assert.IsTrue(history.Entries[0].HasDestinationRecoveryEvidence);
        Assert.IsTrue(history.Entries[0].DestinationIsDurablyCommitted);
        Assert.IsTrue(history.Entries[0].SourceDeleteBarrierMayBeUnresolved);
        Assert.IsFalse(history.HasRetainedSourceDuplicates);
    }

    [TestMethod]
    public void LiveSourceDeleteBarrierIsReportedUnresolvedUntilMoved()
    {
        var live = NewEntry(
            state: FileCrossVolumeMoveEntryState.SourceDeleteStarted,
            destinationIdentity: new FileIdentity(2, 500),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2),
            sourceDeleteStarted: T0.AddSeconds(3));
        var moved = NewEntry(
            state: FileCrossVolumeMoveEntryState.Moved,
            destinationIdentity: new FileIdentity(2, 500),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2),
            sourceDeleteStarted: T0.AddSeconds(3),
            completed: T0.AddSeconds(4));

        Assert.IsTrue(live.SourceDeleteBarrierMayBeUnresolved);
        Assert.IsFalse(moved.SourceDeleteBarrierMayBeUnresolved);
    }

    [TestMethod]
    public void SafeFailureAfterDestinationCommitRetainsSourceDuplicateWithoutRecovery()
    {
        var valid = NewEntry(
            state: FileCrossVolumeMoveEntryState.Failed,
            destinationIdentity: new FileIdentity(2, 500),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2),
            completed: T0.AddSeconds(3),
            failure: Failure);

        var history = NewHistory(
            valid,
            FileCrossVolumeMoveTerminalState.Failed,
            T0.AddSeconds(4));

        Assert.IsFalse(history.RequiresRecovery);
        Assert.IsTrue(history.HasRetainedSourceDuplicates);
        Assert.IsTrue(history.Entries[0].DestinationIsDurablyCommitted);
        Assert.IsFalse(history.Entries[0].HasDestinationRecoveryEvidence);
        Assert.IsFalse(history.Entries[0].SourceDeleteBarrierMayBeUnresolved);
    }

    [TestMethod]
    public void PendingThenLaterSourceDeleteFrontierIsRejected()
    {
        var pending = NewEntryAt(0, "a.txt", FileCrossVolumeMoveEntryState.Pending);
        var laterSourceDelete = NewEntryAt(
            1,
            "b.txt",
            FileCrossVolumeMoveEntryState.SourceDeleteStarted,
            destinationIdentity: new FileIdentity(2, 501),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2),
            sourceDeleteStarted: T0.AddSeconds(3));

        Assert.ThrowsExactly<ArgumentException>(() =>
            NewHistoryEntries(
                FileOperationCollisionPolicy.Stop,
                terminalState: null,
                completedAt: null,
                pending,
                laterSourceDelete));
    }

    [TestMethod]
    public void MovedAfterPendingIsRejected()
    {
        var pending = NewEntryAt(0, "a.txt", FileCrossVolumeMoveEntryState.Pending);
        var laterMoved = NewEntryAt(
            1,
            "b.txt",
            FileCrossVolumeMoveEntryState.Moved,
            destinationIdentity: new FileIdentity(2, 501),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2),
            sourceDeleteStarted: T0.AddSeconds(3),
            completed: T0.AddSeconds(4));

        Assert.ThrowsExactly<ArgumentException>(() =>
            NewHistoryEntries(
                FileOperationCollisionPolicy.Stop,
                terminalState: null,
                completedAt: null,
                pending,
                laterMoved));
    }

    [TestMethod]
    public void SecondActiveFrontierIsRejected()
    {
        var copyStarted = NewEntryAt(
            0,
            "a.txt",
            FileCrossVolumeMoveEntryState.CopyMutationStarted,
            copyStarted: T0.AddSeconds(1));
        var laterDestinationCommitted = NewEntryAt(
            1,
            "b.txt",
            FileCrossVolumeMoveEntryState.DestinationCommitted,
            destinationIdentity: new FileIdentity(2, 501),
            fingerprint: Fingerprint,
            copyStarted: T0.AddSeconds(1),
            destinationCommitted: T0.AddSeconds(2));

        Assert.ThrowsExactly<ArgumentException>(() =>
            NewHistoryEntries(
                FileOperationCollisionPolicy.Stop,
                terminalState: null,
                completedAt: null,
                copyStarted,
                laterDestinationCommitted));
    }

    [TestMethod]
    public void SkippedEntriesAreNeutralAroundExecutableChronology()
    {
        var history = NewHistoryEntries(
            FileOperationCollisionPolicy.Skip,
            terminalState: null,
            completedAt: null,
            NewEntryAt(
                0,
                "skip-before.txt",
                FileCrossVolumeMoveEntryState.Skipped,
                completed: T0.AddSeconds(1)),
            NewEntryAt(
                1,
                "moved.txt",
                FileCrossVolumeMoveEntryState.Moved,
                destinationIdentity: new FileIdentity(2, 501),
                fingerprint: Fingerprint,
                copyStarted: T0.AddSeconds(1),
                destinationCommitted: T0.AddSeconds(2),
                sourceDeleteStarted: T0.AddSeconds(3),
                completed: T0.AddSeconds(4)),
            NewEntryAt(
                2,
                "skip-middle.txt",
                FileCrossVolumeMoveEntryState.Skipped,
                completed: T0.AddSeconds(5)),
            NewEntryAt(3, "pending-a.txt", FileCrossVolumeMoveEntryState.Pending),
            NewEntryAt(
                4,
                "skip-after.txt",
                FileCrossVolumeMoveEntryState.Skipped,
                completed: T0.AddSeconds(6)),
            NewEntryAt(5, "pending-b.txt", FileCrossVolumeMoveEntryState.Pending));

        Assert.AreEqual(6, history.Entries.Count);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Moved, history.Entries[1].State);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Pending, history.Entries[5].State);
    }

    private static FileCrossVolumeMoveActionEntry NewEntry(
        FileCrossVolumeMoveEntryState state,
        FileIdentity? sourceIdentity = null,
        FileIdentity? destinationIdentity = null,
        FileContentFingerprint? fingerprint = null,
        DateTimeOffset? copyStarted = null,
        DateTimeOffset? destinationCommitted = null,
        DateTimeOffset? sourceDeleteStarted = null,
        DateTimeOffset? completed = null,
        FileOperationFailure? failure = null) =>
        new(
            Ordinal: 0,
            Entry: Entry,
            CanonicalSourcePath: @"C:\Source\a.txt",
            CanonicalDestinationPath: @"D:\Destination\a.txt",
            State: state,
            SourceIdentity: sourceIdentity ?? new FileIdentity(1, 100),
            DestinationIdentity: destinationIdentity,
            DestinationContentFingerprint: fingerprint,
            CopyMutationStartedAtUtc: copyStarted,
            DestinationCommittedAtUtc: destinationCommitted,
            SourceDeleteStartedAtUtc: sourceDeleteStarted,
            CompletedAtUtc: completed,
            Failure: failure);

    private static FileCrossVolumeMoveActionEntry NewEntryAt(
        int ordinal,
        string name,
        FileCrossVolumeMoveEntryState state,
        FileIdentity? sourceIdentity = null,
        FileIdentity? destinationIdentity = null,
        FileContentFingerprint? fingerprint = null,
        DateTimeOffset? copyStarted = null,
        DateTimeOffset? destinationCommitted = null,
        DateTimeOffset? sourceDeleteStarted = null,
        DateTimeOffset? completed = null,
        FileOperationFailure? failure = null) =>
        new(
            Ordinal: ordinal,
            Entry: new FileOperationEntry($@"C:\Source\{name}", name, IsDirectory: false),
            CanonicalSourcePath: $@"C:\Source\{name}",
            CanonicalDestinationPath: $@"D:\Destination\{name}",
            State: state,
            SourceIdentity: sourceIdentity ?? new FileIdentity(1, 100UL + (ulong)ordinal),
            DestinationIdentity: destinationIdentity,
            DestinationContentFingerprint: fingerprint,
            CopyMutationStartedAtUtc: copyStarted,
            DestinationCommittedAtUtc: destinationCommitted,
            SourceDeleteStartedAtUtc: sourceDeleteStarted,
            CompletedAtUtc: completed,
            Failure: failure);

    private static FileCrossVolumeMoveActionHistory NewHistory(
        FileCrossVolumeMoveActionEntry entry,
        FileCrossVolumeMoveTerminalState? terminalState = null,
        DateTimeOffset? completedAt = null) =>
        NewHistoryEntries(
            FileOperationCollisionPolicy.Stop,
            terminalState,
            completedAt,
            entry);

    private static FileCrossVolumeMoveActionHistory NewHistoryEntries(
        FileOperationCollisionPolicy collisionPolicy,
        FileCrossVolumeMoveTerminalState? terminalState,
        DateTimeOffset? completedAt,
        params FileCrossVolumeMoveActionEntry[] entries) =>
        new(
            operationId: Guid.NewGuid(),
            queuedAtUtc: T0,
            validatedAtUtc: T0.AddMilliseconds(1),
            startedAtUtc: T0.AddMilliseconds(2),
            completedAtUtc: completedAt,
            collisionPolicy: collisionPolicy,
            sourceDirectoryPath: @"C:\Source",
            destinationDirectoryPath: @"D:\Destination",
            canonicalSourceDirectoryPath: @"C:\Source",
            canonicalDestinationDirectoryPath: @"D:\Destination",
            sourceDirectoryIdentity: new FileIdentity(1, 10),
            destinationDirectoryIdentity: new FileIdentity(2, 20),
            terminalState: terminalState,
            entries: entries);
}