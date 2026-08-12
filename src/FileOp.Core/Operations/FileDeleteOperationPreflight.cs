using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

public sealed record FileDeleteOperationIntent
{
    public FileDeleteOperationIntent(
        string sourcePane,
        Guid sourceTabId,
        string sourceDirectoryPath,
        IEnumerable<FileOperationEntry>? entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePane);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);
        ArgumentNullException.ThrowIfNull(entries);

        var entrySnapshot = entries.ToArray();
        if (entrySnapshot.Length == 0)
        {
            throw new ArgumentException(
                "Delete preflight requires at least one captured source entry.",
                nameof(entries));
        }

        foreach (var entry in entrySnapshot)
        {
            ArgumentNullException.ThrowIfNull(entry);
        }

        SourcePane = sourcePane;
        SourceTabId = sourceTabId;
        SourceDirectoryPath = sourceDirectoryPath;
        Entries = Array.AsReadOnly(entrySnapshot);
    }

    public string SourcePane { get; }

    public Guid SourceTabId { get; }

    public string SourceDirectoryPath { get; }

    public IReadOnlyList<FileOperationEntry> Entries { get; }
}

public sealed record FileDeleteOperationPlan
{
    public FileDeleteOperationPlan(
        Guid id,
        DateTimeOffset queuedAtUtc,
        FileDeleteOperationIntent intent)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Delete preflight plan ID cannot be empty.", nameof(id));
        }
        ArgumentNullException.ThrowIfNull(intent);

        Id = id;
        QueuedAtUtc = queuedAtUtc.ToUniversalTime();
        Intent = intent;
    }

    public Guid Id { get; }

    public DateTimeOffset QueuedAtUtc { get; }

    public FileDeleteOperationIntent Intent { get; }
}

public enum FileDeleteOperationPreflightDecision
{
    ReadyForFurtherReview,
    Blocked,
}

public sealed record FileDeleteOperationPreflightItem(
    FileOperationEntry Entry,
    FileOperationPathInspection Source,
    FileDeleteOperationPreflightDecision Decision,
    string Message);

public enum FileDeleteOperationPreflightStatus
{
    ReadyForFurtherReview,
    Blocked,
}

public sealed record FileDeleteOperationPreflightResult
{
    public FileDeleteOperationPreflightResult(
        FileDeleteOperationPlan plan,
        FileOperationPathInspection sourceDirectory,
        IEnumerable<FileDeleteOperationPreflightItem> items,
        FileDeleteOperationPreflightStatus status,
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
        }

        var blockedCount = itemSnapshot.Count(static item =>
            item.Decision == FileDeleteOperationPreflightDecision.Blocked);
        var expectedStatus = itemSnapshot.Length > 0 && blockedCount == 0
            ? FileDeleteOperationPreflightStatus.ReadyForFurtherReview
            : FileDeleteOperationPreflightStatus.Blocked;
        if (status != expectedStatus)
        {
            throw new ArgumentException(
                "Delete preflight status must match its captured item decisions.",
                nameof(status));
        }

        Plan = plan;
        SourceDirectory = sourceDirectory;
        Items = Array.AsReadOnly(itemSnapshot);
        Status = status;
        Summary = summary;
    }

    public FileDeleteOperationPlan Plan { get; }

    public FileOperationPathInspection SourceDirectory { get; }

    public IReadOnlyList<FileDeleteOperationPreflightItem> Items { get; }

    public FileDeleteOperationPreflightStatus Status { get; }

    public string Summary { get; }

    public int ReadyForFurtherReviewCount => Items.Count(static item =>
        item.Decision == FileDeleteOperationPreflightDecision.ReadyForFurtherReview);

    public int BlockedCount => Items.Count(static item =>
        item.Decision == FileDeleteOperationPreflightDecision.Blocked);

    public bool IsReadyForFurtherReview =>
        Status == FileDeleteOperationPreflightStatus.ReadyForFurtherReview;

    public bool DeleteMutationAuthorized => false;
}

public interface IFileDeleteOperationPreflightValidator
{
    ValueTask<FileDeleteOperationPreflightResult> ValidateAsync(
        FileDeleteOperationPlan plan,
        CancellationToken cancellationToken = default);
}
