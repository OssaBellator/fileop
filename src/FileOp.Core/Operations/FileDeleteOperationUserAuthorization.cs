using System;
using System.Collections.Generic;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public sealed record FileDeleteOperationUserAuthorizationItem(
    FileOperationEntry Entry,
    string CanonicalPath,
    FileIdentity Identity);

public sealed class FileDeleteOperationUserAuthorizationReceipt
{
    internal FileDeleteOperationUserAuthorizationReceipt(
        Guid authorizationId,
        FileDeleteOperationExecutionValidationResult validation,
        DateTimeOffset authorizedAtUtc)
    {
        if (authorizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Delete user authorization ID cannot be empty.",
                nameof(authorizationId));
        }
        ArgumentNullException.ThrowIfNull(validation);
        if (!validation.CanRequestAuthorizationReview ||
            validation.Status != FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview)
        {
            throw new ArgumentException(
                "Delete user authorization requires a ready canonical authorization-review validation result.",
                nameof(validation));
        }
        if (validation.DeleteMutationAuthorized)
        {
            throw new ArgumentException(
                "Canonical validation must remain non-authorizing before user consent is recorded.",
                nameof(validation));
        }
        if (validation.SourceDirectory.Identity is not FileIdentity sourceDirectoryIdentity ||
            validation.SourceDirectory.State != FileOperationCanonicalPathState.Directory ||
            validation.SourceDirectory.IsLeafReparsePoint)
        {
            throw new ArgumentException(
                "Delete user authorization requires current non-reparse source-directory identity evidence.",
                nameof(validation));
        }
        if (validation.Items.Count != validation.Plan.Intent.Entries.Count ||
            validation.Items.Count == 0)
        {
            throw new ArgumentException(
                "Delete user authorization requires complete canonical evidence for every captured plan entry.",
                nameof(validation));
        }

        var itemSnapshot = new FileDeleteOperationUserAuthorizationItem[validation.Items.Count];
        for (var index = 0; index < validation.Items.Count; index++)
        {
            var item = validation.Items[index];
            var plannedEntry = validation.Plan.Intent.Entries[index];
            if (item.Entry != plannedEntry ||
                item.Decision != FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview ||
                item.Source.State != FileOperationCanonicalPathState.File ||
                item.Source.IsLeafReparsePoint ||
                item.Source.Identity is not FileIdentity identity)
            {
                throw new ArgumentException(
                    "Delete user authorization requires complete ready file identity evidence in exact plan order.",
                    nameof(validation));
            }

            itemSnapshot[index] = new FileDeleteOperationUserAuthorizationItem(
                plannedEntry,
                item.Source.CanonicalPath,
                identity);
        }

        AuthorizationId = authorizationId;
        Validation = validation;
        Plan = validation.Plan;
        AuthorizedAtUtc = authorizedAtUtc.ToUniversalTime();
        ValidatedAtUtc = validation.ValidatedAtUtc;
        CanonicalSourceDirectoryPath = validation.SourceDirectory.CanonicalPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        Items = Array.AsReadOnly(itemSnapshot);
    }

    public Guid AuthorizationId { get; }

    public FileDeleteOperationExecutionValidationResult Validation { get; }

    public FileDeleteOperationPlan Plan { get; }

    public Guid PlanId => Plan.Id;

    public DateTimeOffset AuthorizedAtUtc { get; }

    public DateTimeOffset ValidatedAtUtc { get; }

    public string CanonicalSourceDirectoryPath { get; }

    public FileIdentity SourceDirectoryIdentity { get; }

    public IReadOnlyList<FileDeleteOperationUserAuthorizationItem> Items { get; }

    public bool UserAuthorizedAttempt => true;

    public bool DeleteMutationAuthorized => false;

    public bool IsBoundTo(FileDeleteOperationExecutionValidationResult validation) =>
        ReferenceEquals(Validation, validation);
}

public interface IFileDeleteOperationUserAuthorizationIssuer
{
    FileDeleteOperationUserAuthorizationReceipt IssueAfterExplicitUserConfirmation(
        FileDeleteOperationExecutionValidationResult validation);
}

public sealed class FileDeleteOperationUserAuthorizationIssuer : IFileDeleteOperationUserAuthorizationIssuer
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _authorizationIdFactory;

    public FileDeleteOperationUserAuthorizationIssuer()
        : this(TimeProvider.System, Guid.NewGuid)
    {
    }

    public FileDeleteOperationUserAuthorizationIssuer(
        TimeProvider timeProvider,
        Func<Guid> authorizationIdFactory)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(authorizationIdFactory);

        _timeProvider = timeProvider;
        _authorizationIdFactory = authorizationIdFactory;
    }

    public FileDeleteOperationUserAuthorizationReceipt IssueAfterExplicitUserConfirmation(
        FileDeleteOperationExecutionValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var authorizationId = _authorizationIdFactory();
        if (authorizationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Delete user authorization ID source returned an empty ID.");
        }

        return new FileDeleteOperationUserAuthorizationReceipt(
            authorizationId,
            validation,
            _timeProvider.GetUtcNow());
    }
}
