using System;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationHistoryBindingTests
{
    [TestMethod]
    public void ExactPendingHistoryAndStabilityEvidenceBindWithoutGrantingMutationAuthority()
    {
        var authorization = CreateAuthorization(entryCount: 2);
        var stability = CreateStabilityEvidence(authorization, ordinal: 1);
        var history = CreateHistory(authorization);

        var binding = FileDeleteOperationHistoryBinding.Validate(
            authorization,
            stability,
            history,
            ordinal: 1);

        Assert.AreSame(authorization, binding.Authorization);
        Assert.AreSame(stability, binding.StabilityEvidence);
        Assert.AreSame(history, binding.HistorySnapshot);
        Assert.AreEqual(1, binding.Ordinal);
        Assert.AreEqual(authorization.Items[1], binding.AuthorizedItem);
        Assert.AreEqual(history.Entries[1], binding.HistoryEntry);
        Assert.IsFalse(binding.DeleteMutationAuthorized);
        Assert.IsFalse(binding.MutationBarrierSatisfied);
        Assert.IsFalse(binding.StabilityLeaseLivenessProven);
        Assert.IsTrue(binding.IsBoundTo(authorization, stability, history, 1));
    }

    [TestMethod]
    public void StabilityEvidenceFromDifferentReceiptFailsClosed()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var otherAuthorization = CreateAuthorization(entryCount: 1);
        var stability = CreateStabilityEvidence(otherAuthorization, ordinal: 0);
        var history = CreateHistory(authorization);

        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(
                authorization,
                stability,
                history,
                ordinal: 0));
    }

    [TestMethod]
    public void HistoryOperationOrAuthorizationSubstitutionFailsClosed()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var stability = CreateStabilityEvidence(authorization, ordinal: 0);
        var wrongOperation = CreateHistory(authorization, operationId: Guid.NewGuid());
        var wrongAuthorization = CreateHistory(authorization, authorizationId: Guid.NewGuid());

        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, wrongOperation, 0));
        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, wrongAuthorization, 0));
    }

    [TestMethod]
    public void NonPendingOrTerminalHistoryCannotBindForANewBarrier()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var stability = CreateStabilityEvidence(authorization, ordinal: 0);
        var mutationStarted = CreateHistory(
            authorization,
            entryState: FileDeleteOperationActionEntryState.MutationStarted);
        var terminalFailed = CreateHistory(
            authorization,
            entryState: FileDeleteOperationActionEntryState.Failed,
            terminalState: FileDeleteOperationActionTerminalState.Failed);

        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, mutationStarted, 0));
        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, terminalFailed, 0));
    }

    [TestMethod]
    public void RootOrFilePathIdentitySubstitutionFailsClosed()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var stability = CreateStabilityEvidence(authorization, ordinal: 0);
        var wrongRoot = CreateHistory(
            authorization,
            rootIdentity: new FileIdentity(
                authorization.SourceDirectoryIdentity.VolumeSerialNumber,
                authorization.SourceDirectoryIdentity.FileReferenceNumber + 1));
        var wrongFile = CreateHistory(
            authorization,
            fileIdentity: new FileIdentity(
                authorization.Items[0].Identity.VolumeSerialNumber,
                authorization.Items[0].Identity.FileReferenceNumber + 1));
        var wrongPath = CreateHistory(
            authorization,
            canonicalFilePath: authorization.Items[0].CanonicalPath + ".substituted");

        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, wrongRoot, 0));
        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, wrongFile, 0));
        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, wrongPath, 0));
    }

    [TestMethod]
    public void TimingAndSourceProvenanceSubstitutionFailsClosed()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var stability = CreateStabilityEvidence(authorization, ordinal: 0);
        var wrongQueued = CreateHistory(
            authorization,
            queuedAtUtc: authorization.Plan.QueuedAtUtc.AddTicks(1));
        var wrongPane = CreateHistory(authorization, sourcePaneId: "right");
        var wrongTab = CreateHistory(authorization, sourceTabId: Guid.NewGuid());

        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, wrongQueued, 0));
        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, wrongPane, 0));
        AssertThrows<ArgumentException>(() =>
            FileDeleteOperationHistoryBinding.Validate(authorization, stability, wrongTab, 0));
    }

    private static FileDeleteOperationStabilityLeaseEvidence CreateStabilityEvidence(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        int ordinal)
    {
        var request = new FileDeleteOperationStabilityLeaseRequest(authorization, ordinal);
        var item = authorization.Items[ordinal];
        return new FileDeleteOperationStabilityLeaseEvidence(
            request,
            authorization.CanonicalSourceDirectoryPath,
            authorization.SourceDirectoryIdentity,
            item.CanonicalPath,
            item.Identity);
    }

    private static FileDeleteOperationActionHistory CreateHistory(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        Guid? operationId = null,
        Guid? authorizationId = null,
        DateTimeOffset? queuedAtUtc = null,
        string? sourcePaneId = null,
        Guid? sourceTabId = null,
        FileIdentity? rootIdentity = null,
        FileIdentity? fileIdentity = null,
        string? canonicalFilePath = null,
        FileDeleteOperationActionEntryState entryState = FileDeleteOperationActionEntryState.Pending,
        FileDeleteOperationActionTerminalState? terminalState = null)
    {
        var entries = new FileDeleteOperationActionEntry[authorization.Items.Count];
        for (var ordinal = 0; ordinal < entries.Length; ordinal++)
        {
            var item = authorization.Items[ordinal];
            var state = ordinal == 0 ? entryState : FileDeleteOperationActionEntryState.Pending;
            var started = state is FileDeleteOperationActionEntryState.MutationStarted or
                FileDeleteOperationActionEntryState.Committed or
                FileDeleteOperationActionEntryState.RecoveryRequired
                ? authorization.AuthorizedAtUtc.AddSeconds(1)
                : (DateTimeOffset?)null;
            var completed = state is FileDeleteOperationActionEntryState.Committed or
                FileDeleteOperationActionEntryState.Failed or
                FileDeleteOperationActionEntryState.RecoveryRequired
                ? authorization.AuthorizedAtUtc.AddSeconds(2)
                : (DateTimeOffset?)null;
            var failure = state is FileDeleteOperationActionEntryState.Failed or
                FileDeleteOperationActionEntryState.RecoveryRequired
                ? new FileOperationFailure("test", "test failure", item.CanonicalPath, Retryable: false)
                : null;
            entries[ordinal] = new FileDeleteOperationActionEntry(
                ordinal,
                item.Entry,
                ordinal == 0 && canonicalFilePath is not null ? canonicalFilePath : item.CanonicalPath,
                ordinal == 0 && fileIdentity.HasValue ? fileIdentity.Value : item.Identity,
                state,
                started,
                completed,
                failure);
        }

        var completedAt = terminalState.HasValue
            ? authorization.AuthorizedAtUtc.AddSeconds(3)
            : (DateTimeOffset?)null;
        return new FileDeleteOperationActionHistory(
            operationId ?? authorization.PlanId,
            authorizationId ?? authorization.AuthorizationId,
            queuedAtUtc ?? authorization.Plan.QueuedAtUtc,
            authorization.ValidatedAtUtc,
            authorization.AuthorizedAtUtc,
            authorization.AuthorizedAtUtc.AddMilliseconds(1),
            completedAt,
            sourcePaneId ?? authorization.Plan.Intent.SourcePane,
            sourceTabId ?? authorization.Plan.Intent.SourceTabId,
            authorization.CanonicalSourceDirectoryPath,
            rootIdentity ?? authorization.SourceDirectoryIdentity,
            terminalState,
            entries);
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization(int entryCount)
    {
        var entries = new FileOperationEntry[entryCount];
        var validationItems = new FileDeleteOperationExecutionValidationItem[entryCount];
        for (var ordinal = 0; ordinal < entryCount; ordinal++)
        {
            var path = $@"C:\Users\Alice\Temp\bind-{ordinal}.tmp";
            var entry = new FileOperationEntry(path, $"bind-{ordinal}.tmp", IsDirectory: false);
            entries[ordinal] = entry;
            var identity = new FileIdentity(7, checked((ulong)(801 + ordinal)));
            validationItems[ordinal] = new FileDeleteOperationExecutionValidationItem(
                entry,
                new FileOperationCanonicalPath(
                    path,
                    path,
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    identity),
                FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                "ready");
        }

        var intent = new FileDeleteOperationIntent(
            "left",
            Guid.NewGuid(),
            @"C:\Users\Alice\Temp",
            entries);
        var plan = new FileDeleteOperationPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, intent);
        var validation = new FileDeleteOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                intent.SourceDirectoryPath,
                intent.SourceDirectoryPath,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                new FileIdentity(7, 800)),
            validationItems,
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
        return new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }
}
