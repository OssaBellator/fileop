using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

public enum FileDeleteProtectedLocationDecision
{
    AllowedForReview,
    Blocked,
}

public sealed record FileDeleteProtectedLocationResult
{
    public FileDeleteProtectedLocationResult(
        FileDeleteProtectedLocationDecision decision,
        string reason)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Decision = decision;
        Reason = reason;
    }

    public FileDeleteProtectedLocationDecision Decision { get; }

    public string Reason { get; }

    public bool IsBlocked => Decision == FileDeleteProtectedLocationDecision.Blocked;
}

public interface IFileDeleteProtectedLocationPolicy
{
    FileDeleteProtectedLocationResult Evaluate(string canonicalPath);
}

public enum FileDeleteOperationExecutionValidationDecision
{
    ReadyForAuthorizationReview,
    Blocked,
}

public sealed record FileDeleteOperationExecutionValidationItem(
    FileOperationEntry Entry,
    FileOperationCanonicalPath Source,
    FileDeleteOperationExecutionValidationDecision Decision,
    string Message);

public enum FileDeleteOperationExecutionValidationStatus
{
    ReadyForAuthorizationReview,
    Blocked,
}

public sealed record FileDeleteOperationExecutionValidationResult
{
    public FileDeleteOperationExecutionValidationResult(
        FileDeleteOperationPlan plan,
        FileOperationCanonicalPath sourceDirectory,
        IEnumerable<FileDeleteOperationExecutionValidationItem> items,
        FileDeleteOperationExecutionValidationStatus status,
        DateTimeOffset validatedAtUtc,
        string summary)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (!string.Equals(
                sourceDirectory.RequestedPath,
                plan.Intent.SourceDirectoryPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Delete execution-validation source-directory evidence must belong to the exact captured plan.",
                nameof(sourceDirectory));
        }

        var itemSnapshot = items.ToArray();
        if (itemSnapshot.Length != 0 && itemSnapshot.Length != plan.Intent.Entries.Count)
        {
            throw new ArgumentException(
                "Delete execution-validation item evidence must be empty for a root-level block or cover every captured plan entry.",
                nameof(items));
        }
        for (var index = 0; index < itemSnapshot.Length; index++)
        {
            var item = itemSnapshot[index];
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Entry);
            ArgumentNullException.ThrowIfNull(item.Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Message);
            if (!Enum.IsDefined(item.Decision))
            {
                throw new ArgumentOutOfRangeException(nameof(items));
            }

            var plannedEntry = plan.Intent.Entries[index];
            if (item.Entry != plannedEntry)
            {
                throw new ArgumentException(
                    "Delete execution-validation items must preserve the exact captured plan entries and order.",
                    nameof(items));
            }
            if (!string.Equals(
                    item.Source.RequestedPath,
                    plannedEntry.Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Delete execution-validation source evidence must belong to the corresponding captured plan entry.",
                    nameof(items));
            }
            if (item.Decision == FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview &&
                (item.Entry.IsDirectory ||
                 item.Source.State != FileOperationCanonicalPathState.File ||
                 item.Source.IsLeafReparsePoint ||
                 item.Source.Identity is null))
            {
                throw new ArgumentException(
                    "Ready delete execution-validation items require current non-reparse file identity evidence.",
                    nameof(items));
            }
        }

        var sourceDirectoryUsable =
            sourceDirectory.State == FileOperationCanonicalPathState.Directory &&
            !sourceDirectory.IsLeafReparsePoint &&
            sourceDirectory.Identity is not null;
        var blockedCount = itemSnapshot.Count(static item =>
            item.Decision == FileDeleteOperationExecutionValidationDecision.Blocked);
        var expectedStatus = sourceDirectoryUsable &&
            itemSnapshot.Length > 0 &&
            blockedCount == 0
                ? FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview
                : FileDeleteOperationExecutionValidationStatus.Blocked;
        if (status != expectedStatus)
        {
            throw new ArgumentException(
                "Delete execution-validation status must match current canonical source and item evidence.",
                nameof(status));
        }

        Plan = plan;
        SourceDirectory = sourceDirectory;
        Items = Array.AsReadOnly(itemSnapshot);
        Status = status;
        ValidatedAtUtc = validatedAtUtc.ToUniversalTime();
        Summary = summary;
    }

    public FileDeleteOperationPlan Plan { get; }

    public FileOperationCanonicalPath SourceDirectory { get; }

    public IReadOnlyList<FileDeleteOperationExecutionValidationItem> Items { get; }

    public FileDeleteOperationExecutionValidationStatus Status { get; }

    public DateTimeOffset ValidatedAtUtc { get; }

    public string Summary { get; }

    public int ReadyForAuthorizationReviewCount => Items.Count(static item =>
        item.Decision == FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview);

    public int BlockedCount => Items.Count(static item =>
        item.Decision == FileDeleteOperationExecutionValidationDecision.Blocked);

    public bool CanRequestAuthorizationReview =>
        Status == FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview;

    public bool DeleteMutationAuthorized => false;
}

public interface IFileDeleteOperationExecutionValidator
{
    ValueTask<FileDeleteOperationExecutionValidationResult> ValidateAsync(
        FileDeleteOperationPlan plan,
        CancellationToken cancellationToken = default);
}
