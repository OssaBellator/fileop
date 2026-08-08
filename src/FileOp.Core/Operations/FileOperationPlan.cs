using System;
using System.Collections.Generic;

namespace FileOp.Core.Operations;

public enum FileOperationKind
{
    Copy,
    Move,
}

public enum FileOperationCollisionPolicy
{
    Ask,
    Skip,
    Stop,
}

public sealed record FileOperationEntry(
    string Path,
    string Name,
    bool IsDirectory);

public sealed record FileOperationIntent(
    string SourcePane,
    Guid SourceTabId,
    string SourceDirectoryPath,
    IReadOnlyList<FileOperationEntry> Entries,
    string DestinationPane,
    Guid DestinationTabId,
    string DestinationDirectoryPath);

public sealed record FileOperationPlan(
    Guid Id,
    DateTimeOffset QueuedAtUtc,
    FileOperationKind Kind,
    FileOperationCollisionPolicy CollisionPolicy,
    FileOperationIntent Intent);
