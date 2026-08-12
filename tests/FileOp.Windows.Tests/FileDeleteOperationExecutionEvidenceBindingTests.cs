using System;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationExecutionEvidenceBindingTests
{
    [TestMethod]
    public void ExactReceiptStabilityAndPendingHistoryBindAsEvidenceOnly()
    {
        var authorization = CreateAuthorization(entryCount: 2);
        var stability = CreateStabilityEvidence(authorization, ordinal: 1);
        var history = CreateHistory(authorization);
        var binder = new FileDeleteOperationExecutionEvidenceBinder();

        var binding = binder.Bind(authorization, stability, history, ordinal: 1);

        Assert.AreSame(authorization, binding.Authorization);
        Assert.AreSame(stability, binding.StabilityEvidence);
        Assert.AreSame(history, binding.History);
        Assert.AreSame(history.Entries[1], binding.HistoryEntry);
        Assert.AreEqual(authorization.AuthorizationId, binding.AuthorizationId);
        Assert.AreEqual(authorization.PlanId, binding.PlanId);
        Assert.AreEqual(1, binding.Ordinal);
        Assert.IsTrue(binding.IsBoundTo(authorization, stability, history, 1));
        Assert.IsFalse(binding.DeleteMutationAuthorized);
    }

    [TestMethod]
    public void EquivalentIdsDoNotSubstituteForExactAuthorizationReceiptReference()
    {
        var validation = CreateValidation(entryCount: 1);
        var authorizationId = Guid.NewGuid();
        var issuer = new FileDeleteOperationUserAuthorizationIssuer(
            TimeProvider.System,
            () => authorizationId);
        var first = issuer.IssueAfterExplicitUserConfirmation(validation);
        var second = issuer.IssueAfterExplicitUserConfirmation(validation);
        var stability = CreateStabilityEvidence(first, ordinal: 0);
        var history = CreateHistory(first);
        var binder = new FileDeleteOperationExecutionEvidenceBinder();

        Assert.AreEqual(first.AuthorizationId, second.AuthorizationId);
        Assert.AreEqual(first.PlanId, second.PlanId);
        Assert.AreNotSame(first, second);
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(second, stability, history, ordinal: 0));
    }

    [TestMethod]
    public void HistoryMustBelongToExactAuthorizedPlanAndConsentAttempt()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var stability = CreateStabilityEvidence(authorization, ordinal: 0);
        var binder = new FileDeleteOperationExecutionEvidenceBinder();

        var wrongOperation = CreateHistory(
            authorization,
            operationId: Guid.NewGuid());
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, wrongOperation, ordinal: 0));

        var wrongAuthorization = CreateHistory(
            authorization,
            authorizationId: Guid.NewGuid());
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, wrongAuthorization, ordinal: 0));
    }

    [TestMethod]
    public void HistoryMustRemainNonTerminalAndSelectedEntryMustRemainPending()
    {
        var authorization = CreateAuthorization(entryCount: 2);
        var stability = CreateStabilityEvidence(authorization, ordinal: 0);
        var binder = new FileDeleteOperationExecutionEvidenceBinder();

        var mutationStarted = CreateHistory(
            authorization,
            mutationStartedOrdinal: 0);
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, mutationStarted, ordinal: 0));

        var terminal = CreateHistory(
            authorization,
            failedOrdinal: 0,
            terminalFailed: true);
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, terminal, ordinal: 0));
    }

    [TestMethod]
    public void RootAndFilePathOrIdentitySubstitutionFailsClosed()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var stability = CreateStabilityEvidence(authorization, ordinal: 0);
        var binder = new FileDeleteOperationExecutionEvidenceBinder();

        var wrongRootPath = CreateHistory(
            authorization,
            rootPath: @"C:\Users\Alice\Other");
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, wrongRootPath, ordinal: 0));

        var wrongRootIdentity = CreateHistory(
            authorization,
            rootIdentity: new FileIdentity(7, 9999));
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, wrongRootIdentity, ordinal: 0));

        var wrongFilePath = CreateHistory(
            authorization,
            filePath: @"C:\Users\Alice\Temp\other.tmp");
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, wrongFilePath, ordinal: 0));

        var wrongFileIdentity = CreateHistory(
            authorization,
            fileIdentity: new FileIdentity(7, 9998));
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, wrongFileIdentity, ordinal: 0));
    }

    [TestMethod]
    public void OrdinalAndCompleteOrderedEntryShapeAreRequired()
    {
        var authorization = CreateAuthorization(entryCount: 2);
        var stability = CreateStabilityEvidence(authorization, ordinal: 1);
        var history = CreateHistory(authorization);
        var binder = new FileDeleteOperationExecutionEvidenceBinder();

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            binder.Bind(authorization, stability, history, ordinal: 2));
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, history, ordinal: 0));

        var shortHistory = CreateHistory(authorization, entryCountOverride: 1);
        Assert.ThrowsException<InvalidOperationException>(() =>
            binder.Bind(authorization, stability, shortHistory, ordinal: 0));
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization(int entryCount)
    {
        var validation = CreateValidation(entryCount);
        return new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
    }

    private static FileDeleteOperationExecutionValidationResult CreateValidation(int entryCount)
    {
        var entries = new FileOperationEntry[entryCount];
        var items = new FileDeleteOperationExecutionValidationItem[entryCount];
        for (var ordinal = 0; ordinal < entryCount; ordinal++)
        {
            var path = $@"C:\Users\Alice\Temp\bind-{ordinal}.tmp";
            var entry = new FileOperationEntry(path, $"bind-{ordinal}.tmp", IsDirectory: false);
            entries[ordinal] = entry;
            items[ordinal] = new FileDeleteOperationExecutionValidationItem(
                entry,
                new FileOperationCanonicalPath(
                    path,
                    path,
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    new FileIdentity(7, checked((ulong)(701 + ordinal)))),
                FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                "ready");
        }

        var intent = new FileDeleteOperationIntent(
            "left",
            Guid.NewGuid(),
            @"C:\Users\Alice\Temp",
            entries);
        var plan = new FileDeleteOperationPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, intent);
        return new FileDeleteOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                intent.SourceDirectoryPath,
                intent.SourceDirectoryPath,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                new FileIdentity(7, 700)),
            items,
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
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
        string? rootPath = null,
        FileIdentity? rootIdentity = null,
        string? filePath = null,
        FileIdentity? fileIdentity = null,
        int? mutationStartedOrdinal = null,
        int? failedOrdinal = null,
        bool terminalFailed = false,
        int? entryCountOverride = null)
    {
        var now = DateTimeOffset.UtcNow;
        var count = entryCountOverride ?? authorization.Items.Count;
        var entries = new FileDeleteOperationActionEntry[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var authorizedItem = authorization.Items[ordinal];
            var state = FileDeleteOperationActionEntryState.Pending;
            DateTimeOffset? mutationStartedAt = null;
            DateTimeOffset? completedAt = null;
            FileOperationFailure? failure = null;
            if (mutationStartedOrdinal == ordinal)
            {
                state = FileDeleteOperationActionEntryState.MutationStarted;
                mutationStartedAt = now;
            }
            if (failedOrdinal == ordinal)
            {
                state = FileDeleteOperationActionEntryState.Failed;
                completedAt = now;
                failure = new FileOperationFailure(
                    "delete.binding.test",
                    "test failure",
                    authorizedItem.CanonicalPath,
                    Retryable: false);
            }

            entries[ordinal] = new FileDeleteOperationActionEntry(
                ordinal,
                authorizedItem.Entry,
                ordinal == 0 && filePath is not null ? filePath : authorizedItem.CanonicalPath,
                ordinal == 0 && fileIdentity.HasValue ? fileIdentity.Value : authorizedItem.Identity,
                state,
                mutationStartedAt,
                completedAt,
                failure);
        }

        return new FileDeleteOperationActionHistory(
            operationId ?? authorization.PlanId,
            authorizationId ?? authorization.AuthorizationId,
            authorization.Plan.QueuedAtUtc,
            authorization.ValidatedAtUtc,
            authorization.AuthorizedAtUtc,
            now,
            terminalFailed ? now : null,
            authorization.Plan.Intent.SourcePane,
            authorization.Plan.Intent.SourceTabId,
            rootPath ?? authorization.CanonicalSourceDirectoryPath,
            rootIdentity ?? authorization.SourceDirectoryIdentity,
            terminalFailed ? FileDeleteOperationActionTerminalState.Failed : null,
            entries);
    }
}
