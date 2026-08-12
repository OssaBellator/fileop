using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationUserAuthorizationTests
{
    [TestMethod]
    public void ReadyValidationCanRecordUserConsentWithoutGrantingMutationAuthority()
    {
        var validation = CreateReadyValidation();
        var localTime = new DateTimeOffset(2026, 8, 12, 15, 30, 0, TimeSpan.FromHours(10));
        var authorizationId = Guid.NewGuid();

        var receipt = new FileDeleteOperationUserAuthorizationReceipt(
            authorizationId,
            validation,
            localTime);

        Assert.AreEqual(authorizationId, receipt.AuthorizationId);
        Assert.AreSame(validation, receipt.Validation);
        Assert.AreSame(validation.Plan, receipt.Plan);
        Assert.AreEqual(validation.Plan.Id, receipt.PlanId);
        Assert.AreEqual(validation.ValidatedAtUtc, receipt.ValidatedAtUtc);
        Assert.AreEqual(localTime.ToUniversalTime(), receipt.AuthorizedAtUtc);
        Assert.AreEqual(validation.SourceDirectory.CanonicalPath, receipt.CanonicalSourceDirectoryPath);
        Assert.AreEqual(validation.SourceDirectory.Identity!.Value, receipt.SourceDirectoryIdentity);
        Assert.AreEqual(validation.Items.Count, receipt.Items.Count);
        Assert.IsTrue(receipt.UserAuthorizedAttempt);
        Assert.IsTrue(receipt.IsBoundTo(validation));
        Assert.IsFalse(receipt.DeleteMutationAuthorized);

        for (var index = 0; index < receipt.Items.Count; index++)
        {
            Assert.AreEqual(validation.Plan.Intent.Entries[index], receipt.Items[index].Entry);
            Assert.AreEqual(validation.Items[index].Source.CanonicalPath, receipt.Items[index].CanonicalPath);
            Assert.AreEqual(validation.Items[index].Source.Identity!.Value, receipt.Items[index].Identity);
        }
    }

    [TestMethod]
    public void BlockedValidationCannotRecordUserAuthorization()
    {
        var ready = CreateReadyValidation();
        var blockedRoot = new FileOperationCanonicalPath(
            ready.Plan.Intent.SourceDirectoryPath,
            ready.Plan.Intent.SourceDirectoryPath,
            FileOperationCanonicalPathState.Inaccessible,
            IsLeafReparsePoint: false);
        var blocked = new FileDeleteOperationExecutionValidationResult(
            ready.Plan,
            blockedRoot,
            Array.Empty<FileDeleteOperationExecutionValidationItem>(),
            FileDeleteOperationExecutionValidationStatus.Blocked,
            DateTimeOffset.UtcNow,
            "root unavailable");

        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationUserAuthorizationReceipt(
                Guid.NewGuid(),
                blocked,
                DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void ReceiptIsBoundToExactValidationInstanceNotEquivalentRevalidation()
    {
        var original = CreateReadyValidation();
        var receipt = new FileDeleteOperationUserAuthorizationReceipt(
            Guid.NewGuid(),
            original,
            DateTimeOffset.UtcNow);
        var equivalentRevalidation = new FileDeleteOperationExecutionValidationResult(
            original.Plan,
            original.SourceDirectory,
            original.Items,
            original.Status,
            original.ValidatedAtUtc,
            original.Summary);

        Assert.IsTrue(receipt.IsBoundTo(original));
        Assert.IsFalse(receipt.IsBoundTo(equivalentRevalidation));
        Assert.IsFalse(receipt.DeleteMutationAuthorized);
    }

    [TestMethod]
    public void SeparateExplicitConfirmationsNeedSeparateNonEmptyAuthorizationIds()
    {
        var validation = CreateReadyValidation();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = new FileDeleteOperationUserAuthorizationReceipt(
            firstId,
            validation,
            DateTimeOffset.UtcNow);
        var second = new FileDeleteOperationUserAuthorizationReceipt(
            secondId,
            validation,
            DateTimeOffset.UtcNow);

        Assert.AreNotEqual(first.AuthorizationId, second.AuthorizationId);
        Assert.IsTrue(first.UserAuthorizedAttempt);
        Assert.IsTrue(second.UserAuthorizedAttempt);
        Assert.IsFalse(first.DeleteMutationAuthorized);
        Assert.IsFalse(second.DeleteMutationAuthorized);
        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationUserAuthorizationReceipt(
                Guid.Empty,
                validation,
                DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void ReceiptSnapshotsExactOrderedIdentityEvidence()
    {
        var validation = CreateReadyValidation();
        var receipt = new FileDeleteOperationUserAuthorizationReceipt(
            Guid.NewGuid(),
            validation,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(new FileIdentity(7, 100), receipt.SourceDirectoryIdentity);
        CollectionAssert.AreEqual(
            new[] { new FileIdentity(7, 101), new FileIdentity(7, 102) },
            receipt.Items.Select(static item => item.Identity).ToArray());
        CollectionAssert.AreEqual(
            new[] { "a.tmp", "b.tmp" },
            receipt.Items.Select(static item => item.Entry.Name).ToArray());
    }

    private static FileDeleteOperationExecutionValidationResult CreateReadyValidation()
    {
        var first = new FileOperationEntry(@"C:\Users\Alice\Temp\a.tmp", "a.tmp", false);
        var second = new FileOperationEntry(@"C:\Users\Alice\Temp\b.tmp", "b.tmp", false);
        var plan = new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                @"C:\Users\Alice\Temp",
                [first, second]));
        var root = new FileOperationCanonicalPath(
            plan.Intent.SourceDirectoryPath,
            plan.Intent.SourceDirectoryPath,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            new FileIdentity(7, 100));
        var items = new[]
        {
            new FileDeleteOperationExecutionValidationItem(
                first,
                new FileOperationCanonicalPath(
                    first.Path,
                    first.Path,
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    new FileIdentity(7, 101)),
                FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                "ready"),
            new FileDeleteOperationExecutionValidationItem(
                second,
                new FileOperationCanonicalPath(
                    second.Path,
                    second.Path,
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    new FileIdentity(7, 102)),
                FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                "ready"),
        };

        return new FileDeleteOperationExecutionValidationResult(
            plan,
            root,
            items,
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready for explicit user authorization review");
    }
}
