using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum FileOperationCanonicalPathState
{
    Missing,
    File,
    Directory,
    Inaccessible,
    Error,
}

public sealed record FileOperationCanonicalPath(
    string RequestedPath,
    string CanonicalPath,
    FileOperationCanonicalPathState State,
    bool IsLeafReparsePoint,
    FileIdentity? Identity = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool Exists =>
        State is FileOperationCanonicalPathState.File or
            FileOperationCanonicalPathState.Directory;
}

public interface IFileOperationCanonicalPathResolver
{
    ValueTask<FileOperationCanonicalPath> ResolveAsync(
        string path,
        bool allowMissingLeaf = false,
        CancellationToken cancellationToken = default);
}

public enum FileOperationExecutionValidationDecision
{
    Ready,
    Skip,
    NeedsDecision,
    Blocked,
}

public sealed record FileOperationExecutionValidationItem(
    FileOperationEntry Entry,
    FileOperationCanonicalPath Source,
    FileOperationCanonicalPath Destination,
    FileOperationExecutionValidationDecision Decision,
    string Message);

public enum FileOperationExecutionValidationStatus
{
    Ready,
    NeedsDecision,
    Blocked,
}

public sealed record FileOperationExecutionValidationResult
{
    public FileOperationExecutionValidationResult(
        FileOperationPlan plan,
        FileOperationCanonicalPath sourceDirectory,
        FileOperationCanonicalPath destinationDirectory,
        IEnumerable<FileOperationExecutionValidationItem> items,
        FileOperationExecutionValidationStatus status,
        DateTimeOffset validatedAtUtc,
        string summary)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        ArgumentNullException.ThrowIfNull(destinationDirectory);
        ArgumentNullException.ThrowIfNull(items);

        Plan = plan;
        SourceDirectory = sourceDirectory;
        DestinationDirectory = destinationDirectory;
        Items = Array.AsReadOnly(items.ToArray());
        Status = status;
        ValidatedAtUtc = validatedAtUtc;
        Summary = summary;
    }

    public FileOperationPlan Plan { get; }

    public FileOperationCanonicalPath SourceDirectory { get; }

    public FileOperationCanonicalPath DestinationDirectory { get; }

    public IReadOnlyList<FileOperationExecutionValidationItem> Items { get; }

    public FileOperationExecutionValidationStatus Status { get; }

    public DateTimeOffset ValidatedAtUtc { get; }

    public string Summary { get; }

    public int ReadyCount => Items.Count(static item =>
        item.Decision == FileOperationExecutionValidationDecision.Ready);

    public int SkipCount => Items.Count(static item =>
        item.Decision == FileOperationExecutionValidationDecision.Skip);

    public int NeedsDecisionCount => Items.Count(static item =>
        item.Decision == FileOperationExecutionValidationDecision.NeedsDecision);

    public int BlockedCount => Items.Count(static item =>
        item.Decision == FileOperationExecutionValidationDecision.Blocked);

    public bool CanBeginMutation => Status == FileOperationExecutionValidationStatus.Ready;
}

public interface IFileOperationExecutionValidator
{
    ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken = default);
}
