using System;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationStabilityLeaseContractTests
{
    [TestMethod]
    public void EvidenceRequiresExactAuthorizedRootAndFileIdentity()
    {
        var authorization = CreateAuthorization();
        var request = new FileDeleteOperationStabilityLeaseRequest(authorization, 0);
        var item = authorization.Items[0];

        var evidence = new FileDeleteOperationStabilityLeaseEvidence(
            request,
            authorization.CanonicalSourceDirectoryPath,
            authorization.SourceDirectoryIdentity,
            item.CanonicalPath,
            item.Identity);

        Assert.AreSame(authorization, evidence.Authorization);
        Assert.AreEqual(authorization.AuthorizationId, evidence.AuthorizationId);
        Assert.AreEqual(authorization.PlanId, evidence.PlanId);
        Assert.AreEqual(0, evidence.Ordinal);
        Assert.AreEqual(item.Entry, evidence.Entry);
        Assert.IsTrue(evidence.IsBoundTo(authorization, 0));
        Assert.IsFalse(evidence.IsBoundTo(authorization, 1));
        Assert.IsFalse(request.DeleteMutationAuthorized);
        Assert.IsFalse(evidence.DeleteMutationAuthorized);

        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationStabilityLeaseEvidence(
                request,
                authorization.CanonicalSourceDirectoryPath,
                new FileIdentity(
                    authorization.SourceDirectoryIdentity.VolumeSerialNumber,
                    authorization.SourceDirectoryIdentity.FileIndex + 1),
                item.CanonicalPath,
                item.Identity));

        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationStabilityLeaseEvidence(
                request,
                authorization.CanonicalSourceDirectoryPath,
                authorization.SourceDirectoryIdentity,
                item.CanonicalPath,
                new FileIdentity(item.Identity.VolumeSerialNumber, item.Identity.FileIndex + 1)));
    }

    [TestMethod]
    public void EvidenceIsBoundToExactAuthorizationReceiptAndOrdinal()
    {
        var validation = CreateReadyValidation();
        var firstAuthorization = new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
        var secondAuthorization = new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
        var request = new FileDeleteOperationStabilityLeaseRequest(firstAuthorization, 0);
        var item = firstAuthorization.Items[0];
        var evidence = new FileDeleteOperationStabilityLeaseEvidence(
            request,
            firstAuthorization.CanonicalSourceDirectoryPath,
            firstAuthorization.SourceDirectoryIdentity,
            item.CanonicalPath,
            item.Identity);

        Assert.AreNotSame(firstAuthorization, secondAuthorization);
        Assert.IsTrue(evidence.IsBoundTo(firstAuthorization, 0));
        Assert.IsFalse(evidence.IsBoundTo(secondAuthorization, 0));
        Assert.IsFalse(evidence.DeleteMutationAuthorized);
    }

    [TestMethod]
    public void RequestRejectsOutOfRangeOrdinal()
    {
        var authorization = CreateAuthorization();

        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new FileDeleteOperationStabilityLeaseRequest(authorization, -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new FileDeleteOperationStabilityLeaseRequest(authorization, authorization.Items.Count));
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization() =>
        new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(CreateReadyValidation());

    private static FileDeleteOperationExecutionValidationResult CreateReadyValidation()
    {
        var entry = new FileOperationEntry(
            @"C:\Users\Alice\Temp\lease.tmp",
            "lease.tmp",
            IsDirectory: false);
        var plan = new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                @"C:\Users\Alice\Temp",
                new[] { entry }));
        var rootIdentity = new FileIdentity(17, 1700);
        var fileIdentity = new FileIdentity(17, 1701);
        var root = new FileOperationCanonicalPath(
            plan.Intent.SourceDirectoryPath,
            plan.Intent.SourceDirectoryPath,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            rootIdentity);
        var item = new FileDeleteOperationExecutionValidationItem(
            entry,
            new FileOperationCanonicalPath(
                entry.Path,
                entry.Path,
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                fileIdentity),
            FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
            "ready");

        return new FileDeleteOperationExecutionValidationResult(
            plan,
            root,
            new[] { item },
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready for explicit user authorization");
    }
}
