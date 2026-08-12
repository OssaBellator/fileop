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

        var itemSnapshot = items.ToArray();
        foreach (var item in itemSnapshot)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(item.Entry);
            ArgumentNullException.ThrowIfNull(item.Source);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Message);
            if (!Enum.IsDefined(item.Decision))
            {
                throw new ArgumentOutOfRangeException(nameof(items));
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
