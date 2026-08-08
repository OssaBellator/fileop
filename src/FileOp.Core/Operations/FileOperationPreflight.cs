using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

public enum FileOperationPathState
{
    Missing,
    File,
    Directory,
    Inaccessible,
    Error,
}

public sealed record FileOperationPathInspection(
    string Path,
    FileOperationPathState State,
    bool IsReparsePoint,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool Exists => State is FileOperationPathState.File or FileOperationPathState.Directory;
}

public interface IFileOperationPathProbe
{
    ValueTask<FileOperationPathInspection> InspectAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public enum FileOperationPreflightDecision
{
    Ready,
    Skip,
    NeedsDecision,
    Blocked,
}

public sealed record FileOperationPreflightItem(
    FileOperationEntry Entry,
    string DestinationPath,
    FileOperationPathInspection Source,
    FileOperationPathInspection Destination,
    FileOperationPreflightDecision Decision,
    string Message);

public enum FileOperationPreflightStatus
{
    Ready,
    NeedsDecision,
    Blocked,
}

public sealed record FileOperationPreflightResult(
    FileOperationPlan Plan,
    FileOperationPathInspection SourceDirectory,
    FileOperationPathInspection DestinationDirectory,
    IReadOnlyList<FileOperationPreflightItem> Items,
    FileOperationPreflightStatus Status,
    string Summary)
{
    public int ReadyCount => Items.Count(static item => item.Decision == FileOperationPreflightDecision.Ready);

    public int SkipCount => Items.Count(static item => item.Decision == FileOperationPreflightDecision.Skip);

    public int NeedsDecisionCount => Items.Count(static item => item.Decision == FileOperationPreflightDecision.NeedsDecision);

    public int BlockedCount => Items.Count(static item => item.Decision == FileOperationPreflightDecision.Blocked);

    public bool CanProceedToExecutionValidation => Status == FileOperationPreflightStatus.Ready;
}

public interface IFileOperationPreflightValidator
{
    ValueTask<FileOperationPreflightResult> ValidateAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken = default);
}
