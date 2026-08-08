using System;
using System.Collections.Generic;
using System.Linq;

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

public sealed record FileOperationIntent
{
    public FileOperationIntent(
        string sourcePane,
        Guid sourceTabId,
        string sourceDirectoryPath,
        IEnumerable<FileOperationEntry>? entries,
        string destinationPane,
        Guid destinationTabId,
        string destinationDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var entrySnapshot = entries.ToArray();

        SourcePane = sourcePane;
        SourceTabId = sourceTabId;
        SourceDirectoryPath = sourceDirectoryPath;
        Entries = Array.AsReadOnly(entrySnapshot);
        DestinationPane = destinationPane;
        DestinationTabId = destinationTabId;
        DestinationDirectoryPath = destinationDirectoryPath;
    }

    public string SourcePane { get; }

    public Guid SourceTabId { get; }

    public string SourceDirectoryPath { get; }

    public IReadOnlyList<FileOperationEntry> Entries { get; }

    public string DestinationPane { get; }

    public Guid DestinationTabId { get; }

    public string DestinationDirectoryPath { get; }
}

public sealed record FileOperationPlan(
    Guid Id,
    DateTimeOffset QueuedAtUtc,
    FileOperationKind Kind,
    FileOperationCollisionPolicy CollisionPolicy,
    FileOperationIntent Intent);
