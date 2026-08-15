using System;
using FileOp.Core.Indexing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexRebuildPublicationTransactionTests
{
    [TestMethod]
    public void VerifiedShadowWithExclusiveLeaseCanPrepareAndPublish()
    {
        var publication = PublishableState();
        var id = Guid.NewGuid();

        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(id, publication);
        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(prepared, publication);
        var published = IndexRebuildPublicationTransactionPolicy.MarkPublished(started, publication);

        Assert.AreEqual(id, published.AttemptId);
        Assert.AreEqual(publication.LiveDatabasePath, published.LiveDatabasePath);
        Assert.AreEqual(publication.ShadowDatabasePath, published.ShadowDatabasePath);
        Assert.AreEqual(IndexRebuildPublicationAttemptState.Published, published.State);
        Assert.IsTrue(published.LiveSnapshotWasReadableAtPreparation);
        Assert.IsTrue(published.ShadowCheckpointWasValidAtPreparation);
        Assert.IsTrue(published.ExclusivePublicationLeaseWasHeldAtPreparation);
        Assert.IsTrue(published.SwapMayHaveChangedLiveSnapshot);
        Assert.IsFalse(published.RequiresRecovery);
        Assert.IsFalse(published.GrantsAutomaticRetryAuthority);
        Assert.IsFalse(published.GrantsRollbackAuthority);
        Assert.IsFalse(published.GrantsCleanupAuthority);
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
            IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(prepared, leaseLost));

        Assert.AreEqual(typeof(InvalidOperationException), exception.GetType());
        StringAssert.Contains(exception.Message, "exclusive publication lease evidence has been lost");
        Assert.AreEqual(IndexRebuildPublicationAttemptState.Prepared, prepared.State);
        Assert.IsFalse(prepared.SwapMayHaveChangedLiveSnapshot);
    }

    [TestMethod]
    public void PublicationPathRebindingIsRejectedBeforeAndAfterSwapStart()
    {
        var publishable = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publishable);
        var rebound = publishable with { ShadowDatabasePath = @"C:\index\other-shadow.sqlite" };

        var before = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(prepared, rebound));
        Assert.AreEqual(typeof(InvalidOperationException), before.GetType());
        StringAssert.Contains(before.Message, "not bound to the current live/shadow database paths");

        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(prepared, publishable);
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
            "shadow close failed");

        Assert.AreEqual(IndexRebuildPublicationAttemptState.AbandonedBeforeSwap, abandoned.State);
        Assert.AreEqual("shadow close failed", abandoned.FailureSummary);
        Assert.IsFalse(abandoned.SwapMayHaveChangedLiveSnapshot);
        Assert.IsFalse(abandoned.RequiresRecovery);
        Assert.IsFalse(abandoned.GrantsAutomaticRetryAuthority);
    }

    [TestMethod]
    public void FailureAfterSwapStartIsRecoverySensitiveAndCannotBeSafelyAbandoned()
    {
        var publication = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publication);
        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(prepared, publication);

        var abandonedException = CaptureException(() =>
            IndexRebuildPublicationTransactionPolicy.AbandonBeforeSwap(started, "not safe"));
        Assert.AreEqual(typeof(InvalidOperationException), abandonedException.GetType());

        var recovery = IndexRebuildPublicationTransactionPolicy.MarkRecoveryRequired(
            started,
            "filesystem replacement outcome is ambiguous");

        Assert.AreEqual(IndexRebuildPublicationAttemptState.RecoveryRequired, recovery.State);
        Assert.IsTrue(recovery.SwapMayHaveChangedLiveSnapshot);
        Assert.IsTrue(recovery.RequiresRecovery);
        Assert.AreEqual("filesystem replacement outcome is ambiguous", recovery.FailureSummary);
        Assert.IsFalse(recovery.GrantsAutomaticRetryAuthority);
        Assert.IsFalse(recovery.GrantsRollbackAuthority);
        Assert.IsFalse(recovery.GrantsCleanupAuthority);
    }

    [TestMethod]
    public void PublishedOrRecoveryAttemptsCannotTransitionAgain()
    {
        var publication = PublishableState();
        var prepared = IndexRebuildPublicationTransactionPolicy.Prepare(Guid.NewGuid(), publication);
        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(prepared, publication);
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
        var started = IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(prepared, publication);

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
