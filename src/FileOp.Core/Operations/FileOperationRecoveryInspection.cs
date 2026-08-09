using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum FileOperationRecoveryDestinationStatus
{
    NoVerifiedIdentity,
    Missing,
    SameObject,
    DifferentObject,
    Redirected,
    ReparsePoint,
    UnexpectedType,
    Inaccessible,
    Error,
}

public sealed record FileOperationRecoveryInspectionItem(
    int Ordinal,
    FileOperationActionEntry Entry,
    FileOperationRecoveryDestinationStatus Status,
    FileOperationCanonicalPath CurrentDestination,
    string Message)
{
    public FileIdentity? RecordedDestinationIdentity =>
        Entry.State == FileOperationActionEntryState.RecoveryRequired
            ? Entry.DestinationIdentity
            : null;

    public bool IsSameRecordedObject =>
        Status == FileOperationRecoveryDestinationStatus.SameObject;
}

public sealed record FileOperationRecoveryInspection
{
    public FileOperationRecoveryInspection(
        Guid operationId,
        IEnumerable<FileOperationRecoveryInspectionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        OperationId = operationId;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public Guid OperationId { get; }

    public IReadOnlyList<FileOperationRecoveryInspectionItem> Items { get; }

    public int SameObjectCount => Items.Count(static item => item.IsSameRecordedObject);
}

public interface IFileOperationRecoveryInspector
{
    ValueTask<FileOperationRecoveryInspection> InspectAsync(
        FileOperationActionHistory history,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs read-only inspection of recovery-sensitive Copy destinations.
/// Identity equality is evidence only; this type grants no mutation or Undo authority.
/// </summary>
public sealed class FileOperationRecoveryInspector : IFileOperationRecoveryInspector
{
    private readonly IFileOperationCanonicalPathResolver _resolver;

    public FileOperationRecoveryInspector(IFileOperationCanonicalPathResolver resolver) =>
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public async ValueTask<FileOperationRecoveryInspection> InspectAsync(
        FileOperationActionHistory history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Kind != FileOperationKind.Copy)
        {
            throw new NotSupportedException("Recovery destination inspection currently supports Copy history only.");
        }

        var results = new List<FileOperationRecoveryInspectionItem>();
        foreach (var entry in history.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.State is not FileOperationActionEntryState.MutationStarted and
                not FileOperationActionEntryState.RecoveryRequired)
            {
                continue;
            }

            if (entry.Entry.IsDirectory)
            {
                throw new InvalidOperationException(
                    "Recovery destination inspection does not support directory mutation history.");
            }

            var current = await _resolver
                .ResolveAsync(
                    entry.CanonicalDestinationPath,
                    allowMissingLeaf: true,
                    cancellationToken)
                .ConfigureAwait(false);
            var status = Classify(entry, current);
            results.Add(new FileOperationRecoveryInspectionItem(
                entry.Ordinal,
                entry,
                status,
                current,
                Describe(status)));
        }

        return new FileOperationRecoveryInspection(history.OperationId, results);
    }

    private static FileOperationRecoveryDestinationStatus Classify(
        FileOperationActionEntry entry,
        FileOperationCanonicalPath current)
    {
        if (current.Exists && current.IsLeafReparsePoint)
        {
            return FileOperationRecoveryDestinationStatus.ReparsePoint;
        }

        var hasCanonicalLocation = current.State is
            FileOperationCanonicalPathState.Missing or
            FileOperationCanonicalPathState.File or
            FileOperationCanonicalPathState.Directory;
        if (hasCanonicalLocation &&
            !PathsEqual(entry.CanonicalDestinationPath, current.CanonicalPath))
        {
            return FileOperationRecoveryDestinationStatus.Redirected;
        }

        return current.State switch
        {
            FileOperationCanonicalPathState.Missing =>
                FileOperationRecoveryDestinationStatus.Missing,
            FileOperationCanonicalPathState.Inaccessible =>
                FileOperationRecoveryDestinationStatus.Inaccessible,
            FileOperationCanonicalPathState.Error =>
                FileOperationRecoveryDestinationStatus.Error,
            FileOperationCanonicalPathState.Directory =>
                FileOperationRecoveryDestinationStatus.UnexpectedType,
            FileOperationCanonicalPathState.File => ClassifyExistingFile(entry, current),
            _ => FileOperationRecoveryDestinationStatus.Error,
        };
    }

    private static FileOperationRecoveryDestinationStatus ClassifyExistingFile(
        FileOperationActionEntry entry,
        FileOperationCanonicalPath current)
    {
        if (entry.State != FileOperationActionEntryState.RecoveryRequired ||
            entry.DestinationIdentity is not FileIdentity expected ||
            current.Identity is not FileIdentity actual)
        {
            return FileOperationRecoveryDestinationStatus.NoVerifiedIdentity;
        }

        return actual == expected
            ? FileOperationRecoveryDestinationStatus.SameObject
            : FileOperationRecoveryDestinationStatus.DifferentObject;
    }

    private static string Describe(FileOperationRecoveryDestinationStatus status) => status switch
    {
        FileOperationRecoveryDestinationStatus.NoVerifiedIdentity =>
            "A destination exists, but durable history does not contain enough verified stable identity evidence to correlate it.",
        FileOperationRecoveryDestinationStatus.Missing =>
            "The recorded destination path is currently missing and still resolves through the recorded canonical namespace.",
        FileOperationRecoveryDestinationStatus.SameObject =>
            "The current destination resolves to the recorded canonical location and stable FileIdentity. This is evidence only, not deletion authorization.",
        FileOperationRecoveryDestinationStatus.DifferentObject =>
            "The destination path exists but names a different stable FileIdentity than durable recovery history recorded.",
        FileOperationRecoveryDestinationStatus.Redirected =>
            "The destination path now resolves through a different canonical location than durable recovery history recorded.",
        FileOperationRecoveryDestinationStatus.ReparsePoint =>
            "The destination leaf is currently a reparse point and is not treated as the recorded recovery object.",
        FileOperationRecoveryDestinationStatus.UnexpectedType =>
            "The recorded file destination currently resolves to a directory.",
        FileOperationRecoveryDestinationStatus.Inaccessible =>
            "The destination could not be inspected because access was denied.",
        FileOperationRecoveryDestinationStatus.Error =>
            "The destination could not be inspected reliably.",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            TrimTrailingSeparators(left),
            TrimTrailingSeparators(right),
            StringComparison.OrdinalIgnoreCase);

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd('\\', '/');
}
