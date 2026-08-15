using System;
using FileOp.Core.Indexing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexRebuildPublicationTransactionTests
{
    [TestMethod]
    public void VerifiedQuiescedShadowCanPrepareAndPublish()
    {
        var publication = PublishableState();
        var id = Guid.NewGuid();
        var quiescence = FullyQuiesced(publication);

        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(id, publication);
        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
            prepared,
            publication,
            quiescence);
        var published = IndexRebuildPublicationTransactionPolicy.MarkPublished(started, publication);

        Assert.AreEqual(id, published.AttemptId);
        Assert.AreEqual(IndexRebuildPublicationAttemptState.Published, published.State);
        Assert.AreSame(quiescence, published.QuiescenceAtSwapStart);
        Assert.IsTrue(quiescence.CanStartFilesystemSwap);
        Assert.IsFalse(quiescence.GrantsFilesystemMutationAuthority);
        Assert.IsTrue(published.SwapMayHaveChangedLiveSnapshot);
        Assert.IsFalse(published.RequiresRecovery);
        Assert.IsFalse(published.GrantsAutomaticRetryAuthority);
        Assert.IsFalse(published.GrantsRollbackAuthority);
        Assert.IsFalse(published.GrantsCleanupAuthority);
        Assert.IsFalse(published.GrantsFilesystemMutationAuthority);
    }

    [TestMethod]
    public void NonPublishableStateCannotPrepareAttempt()
    {
        var state = IndexRebuildPublicationPolicy.Begin(@"C:\index\live.sqlite", @"C:\index\shadow.sqlite");
        var exception = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), state));

        Assert.AreEqual(typeof(InvalidOperationException), exception.GetType());
        StringAssert.Contains(exception.Message, "cannot be prepared");
    }

    [TestMethod]
    public void EmptyAttemptIdIsRejected()
    {
        var exception = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.Prepare(Guid.Empty, PublishableState()));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "must be non-empty");
    }

    [TestMethod]
    public void LosingExclusiveLeaseBeforeSwapStartFailsClosed()
    {
        var publishable = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publishable);
        var leaseLost = publishable with { ExclusivePublicationLeaseHeld = false };
        var exception = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
                prepared,
                leaseLost,
                FullyQuiesced(publishable)));

        Assert.AreEqual(typeof(InvalidOperationException), exception.GetType());
        StringAssert.Contains(exception.Message, "exclusive publication lease evidence has been lost");
        Assert.AreEqual(IndexRebuildPublicationAttemptState.Prepared, prepared.State);
        Assert.IsFalse(prepared.SwapMayHaveChangedLiveSnapshot);
    }

    [TestMethod]
    public void EverySqliteQuiescenceRequirementIsMandatoryBeforeSwapStart()
    {
        var publication = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publication);
        var complete = FullyQuiesced(publication);
        var incomplete = new[]
        {
            complete with { LocalVolumeOperationGateHeld = false },
            complete with { CrossProcessMaintenanceLeaseHeld = false },
            complete with { LiveWalCheckpointComplete = false },
            complete with { ShadowWalCheckpointComplete = false },
            complete with { SqliteConnectionPoolsCleared = false },
            complete with { LiveWalAndShmSidecarsQuiesced = false },
            complete with { ShadowWalAndShmSidecarsQuiesced = false },
        };

        foreach (var evidence in incomplete)
        {
            Assert.IsFalse(evidence.CanStartFilesystemSwap);
            var exception = CaptureException(() =>
                IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
                    prepared,
                    publication,
                    evidence));
            Assert.AreEqual(typeof(InvalidOperationException), exception.GetType());
            StringAssert.Contains(exception.Message, "SQLite connection pools are cleared");
        }
    }

    [TestMethod]
    public void QuiescencePathRebindingIsRejected()
    {
        var publication = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publication);
        var rebound = FullyQuiesced(publication) with
        {
            ShadowDatabasePath = @"C:\index\other-shadow.sqlite",
        };

        var exception = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
                prepared,
                publication,
                rebound));

        Assert.AreEqual(typeof(InvalidOperationException), exception.GetType());
        StringAssert.Contains(exception.Message, "quiescence evidence is not bound");
    }

    [TestMethod]
    public void PublicationPathRebindingIsRejectedBeforeAndAfterSwapStart()
    {
        var publishable = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publishable);
        var rebound = publishable with { ShadowDatabasePath = @"C:\index\other-shadow.sqlite" };

        var before = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
                prepared,
                rebound,
                FullyQuiesced(publishable)));
        Assert.AreEqual(typeof(InvalidOperationException), before.GetType());
        StringAssert.Contains(before.Message, "not bound to the current live/shadow database paths");

        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
            prepared,
            publishable,
            FullyQuiesced(publishable));
        var after = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.MarkPublished(started, rebound));
        Assert.AreEqual(typeof(InvalidOperationException), after.GetType());
        StringAssert.Contains(after.Message, "not bound to the current live/shadow database paths");
    }

    [TestMethod]
    public void PreparedAttemptCanBeSafelyAbandonedBeforeSwap()
    {
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(
            Guid.NewGuid(),
            PublishableState());

        var abandoned = IndexRebuildPublicationTransactionPolicy.AbandonBeforeSwap(
            prepared,
            "SQLite quiescence could not be established");

        Assert.AreEqual(IndexRebuildPublicationAttemptState.AbandonedBeforeSwap, abandoned.State);
        Assert.IsNull(abandoned.QuiescenceAtSwapStart);
        Assert.IsFalse(abandoned.SwapMayHaveChangedLiveSnapshot);
        Assert.IsFalse(abandoned.RequiresRecovery);
        Assert.IsFalse(abandoned.GrantsAutomaticRetryAuthority);
    }

    [TestMethod]
    public void FailureAfterSwapStartIsRecoverySensitiveAndCannotBeSafelyAbandoned()
    {
        var publication = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publication);
        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
            prepared,
            publication,
            FullyQuiesced(publication));

        var abandonedException = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.AbandonBeforeSwap(started, "not safe"));
        Assert.AreEqual(typeof(InvalidOperationException), abandonedException.GetType());

        var recovery = IndexRebuildPublicationTransactionPolicy.MarkRecoveryRequired(
            started,
            "filesystem replacement outcome is ambiguous");

        Assert.AreEqual(IndexRebuildPublicationAttemptState.RecoveryRequired, recovery.State);
        Assert.IsTrue(recovery.SwapMayHaveChangedLiveSnapshot);
        Assert.IsTrue(recovery.RequiresRecovery);
        Assert.IsNotNull(recovery.QuiescenceAtSwapStart);
        Assert.IsFalse(recovery.GrantsAutomaticRetryAuthority);
        Assert.IsFalse(recovery.GrantsRollbackAuthority);
        Assert.IsFalse(recovery.GrantsCleanupAuthority);
    }

    [TestMethod]
    public void PublishedOrRecoveryAttemptsCannotTransitionAgain()
    {
        var publication = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publication);
        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
            prepared,
            publication,
            FullyQuiesced(publication));
        var published = IndexRebuildPublicationTransactionPolicy.MarkPublished(started, publication);
        var recovery = IndexRebuildPublicationTransactionPolicy.MarkRecoveryRequired(started, "ambiguous");

        Assert.AreEqual(
            typeof(InvalidOperationException),
            CaptureException(() =>
                IndexRebuildPublicationTransactionPolicy.MarkRecoveryRequired(published, "late")).GetType());
        Assert.AreEqual(
            typeof(InvalidOperationException),
            CaptureException(() =>
                IndexRebuildPublicationTransactionPolicy.MarkPublished(recovery, publication)).GetType());
    }

    [TestMethod]
    public void FailureSettlementRequiresSummary()
    {
        var publication = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publication);
        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
            prepared,
            publication,
            FullyQuiesced(publication));

        Assert.AreEqual(
            typeof(ArgumentException),
            CaptureException(() =>
                IndexRebuildPublicationTransactionPolicy.AbandonBeforeSwap(prepared, " ")).GetType());
        Assert.AreEqual(
            typeof(ArgumentException),
            CaptureException(() =>
                IndexRebuildPublicationTransactionPolicy.MarkRecoveryRequired(started, " ")).GetType());
    }

    private static IndexRebuildPublicationState PublishableState() =>
        IndexRebuildPublicationPolicy.MarkVerified(
            IndexRebuildPublicationPolicy.Begin(
                @"C:\index\live.sqlite",
                @"C:\index\shadow.sqlite"),
            exclusivePublicationLeaseHeld: true);

    private static IndexRebuildPublicationQuiescenceEvidence FullyQuiesced(
        IndexRebuildPublicationState publication) =>
        new(
            publication.LiveDatabasePath,
            publication.ShadowDatabasePath,
            LocalVolumeOperationGateHeld: true,
            CrossProcessMaintenanceLeaseHeld: true,
            LiveWalCheckpointComplete: true,
            ShadowWalCheckpointComplete: true,
            SqliteConnectionPoolsCleared: true,
            LiveWalAndShmSidecarsQuiesced: true,
            ShadowWalAndShmSidecarsQuiesced: true);

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        Assert.Fail("Expected operation to throw.");
        throw new InvalidOperationException("Assert.Fail returned unexpectedly.");
    }
}
