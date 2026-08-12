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
        var authorizationId = Guid.NewGuid();
        var authorizedAt = new DateTimeOffset(2026, 8, 12, 5, 30, 0, TimeSpan.Zero);
        var issuer = CreateIssuer(authorizationId, authorizedAt);

        var receipt = issuer.IssueAfterExplicitUserConfirmation(validation);

        Assert.AreEqual(authorizationId, receipt.AuthorizationId);
        Assert.AreSame(validation, receipt.Validation);
        Assert.AreSame(validation.Plan, receipt.Plan);
        Assert.AreEqual(validation.Plan.Id, receipt.PlanId);
        Assert.AreEqual(validation.ValidatedAtUtc, receipt.ValidatedAtUtc);
        Assert.AreEqual(authorizedAt, receipt.AuthorizedAtUtc);
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
        var issuer = CreateIssuer(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.ThrowsException<ArgumentException>(() =>
            issuer.IssueAfterExplicitUserConfirmation(blocked));
    }

    [TestMethod]
    public void ReceiptIsBoundToExactValidationInstanceNotEquivalentRevalidation()
    {
        var original = CreateReadyValidation();
        var receipt = CreateIssuer(Guid.NewGuid(), DateTimeOffset.UtcNow)
            .IssueAfterExplicitUserConfirmation(original);
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
    public void IssuerOwnsFreshAuthorizationIdAndUtcObservation()
    {
        var validation = CreateReadyValidation();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var issuedIds = new Queue<Guid>([firstId, secondId]);
        var authorizedAt = new DateTimeOffset(2026, 8, 12, 5, 45, 0, TimeSpan.Zero);
        var issuer = new FileDeleteOperationUserAuthorizationIssuer(
            new FixedTimeProvider(authorizedAt),
            () => issuedIds.Dequeue());

        var first = issuer.IssueAfterExplicitUserConfirmation(validation);
        var second = issuer.IssueAfterExplicitUserConfirmation(validation);

        Assert.AreEqual(firstId, first.AuthorizationId);
        Assert.AreEqual(secondId, second.AuthorizationId);
        Assert.AreNotEqual(first.AuthorizationId, second.AuthorizationId);
        Assert.AreEqual(authorizedAt, first.AuthorizedAtUtc);
        Assert.AreEqual(authorizedAt, second.AuthorizedAtUtc);
        Assert.IsTrue(first.UserAuthorizedAttempt);
        Assert.IsTrue(second.UserAuthorizedAttempt);
        Assert.IsFalse(first.DeleteMutationAuthorized);
        Assert.IsFalse(second.DeleteMutationAuthorized);

        var invalidIssuer = CreateIssuer(Guid.Empty, authorizedAt);
        Assert.ThrowsException<InvalidOperationException>(() =>
            invalidIssuer.IssueAfterExplicitUserConfirmation(validation));
    }

    [TestMethod]
    public void ReceiptSnapshotsExactOrderedIdentityEvidence()
    {
        var validation = CreateReadyValidation();
        var receipt = CreateIssuer(Guid.NewGuid(), DateTimeOffset.UtcNow)
            .IssueAfterExplicitUserConfirmation(validation);

        Assert.AreEqual(new FileIdentity(7, 100), receipt.SourceDirectoryIdentity);
        CollectionAssert.AreEqual(
            new[] { new FileIdentity(7, 101), new FileIdentity(7, 102) },
            receipt.Items.Select(static item => item.Identity).ToArray());
        CollectionAssert.AreEqual(
            new[] { "a.tmp", "b.tmp" },
            receipt.Items.Select(static item => item.Entry.Name).ToArray());
    }

    private static FileDeleteOperationUserAuthorizationIssuer CreateIssuer(
        Guid authorizationId,
        DateTimeOffset authorizedAt) =>
        new(
            new FixedTimeProvider(authorizedAt),
            () => authorizationId);

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow.ToUniversalTime();
    }
}
