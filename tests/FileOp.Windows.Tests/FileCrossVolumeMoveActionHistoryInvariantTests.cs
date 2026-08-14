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

        Assert.ThrowsException<ArgumentException>(() => NewHistory(invalid));
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

        Assert.ThrowsException<ArgumentException>(() => NewHistory(invalid));
    }

    [TestMethod]
    public void SourceIdentityMustRemainBoundToDurableSourceRootVolume()
    {
        var invalid = NewEntry(
            state: FileCrossVolumeMoveEntryState.Pending,
            sourceIdentity: new FileIdentity(9, 100));

        Assert.ThrowsException<ArgumentException>(() => NewHistory(invalid));
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

        Assert.ThrowsException<ArgumentException>(() => NewHistory(invalid));
    }

    [TestMethod]
    public void CopyBarrierRecoveryMayDurablyCaptureObservedDestinationEvidence()
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
        Assert.IsTrue(history.Entries[0].DestinationIsDurablyCommitted);
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
            Entry,
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

    private static FileCrossVolumeMoveActionHistory NewHistory(
        FileCrossVolumeMoveActionEntry entry,
        FileCrossVolumeMoveTerminalState? terminalState = null,
        DateTimeOffset? completedAt = null) =>
        new(
            operationId: Guid.NewGuid(),
            queuedAtUtc: T0,
            validatedAtUtc: T0.AddMilliseconds(1),
            startedAtUtc: T0.AddMilliseconds(2),
            completedAtUtc: completedAt,
            collisionPolicy: FileOperationCollisionPolicy.Stop,
            sourceDirectoryPath: @"C:\Source",
            destinationDirectoryPath: @"D:\Destination",
            canonicalSourceDirectoryPath: @"C:\Source",
            canonicalDestinationDirectoryPath: @"D:\Destination",
            sourceDirectoryIdentity: new FileIdentity(1, 10),
            destinationDirectoryIdentity: new FileIdentity(2, 20),
            terminalState,
            new[] { entry });
}
