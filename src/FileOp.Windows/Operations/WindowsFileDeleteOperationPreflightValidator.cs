using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

public sealed class WindowsFileDeleteOperationPreflightValidator : IFileDeleteOperationPreflightValidator
{
    private readonly IFileOperationPathProbe _probe;

    public WindowsFileDeleteOperationPreflightValidator(IFileOperationPathProbe? probe = null)
    {
        _probe = probe ?? new WindowsFileOperationPathProbe();
    }

    public async ValueTask<FileDeleteOperationPreflightResult> ValidateAsync(
        FileDeleteOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Intent);
        ArgumentNullException.ThrowIfNull(plan.Intent.Entries);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalize(plan.Intent.SourceDirectoryPath, out var sourceDirectory, out var pathError))
        {
            return BlockPlan(
                plan,
                ErrorInspection(plan.Intent.SourceDirectoryPath, "InvalidSourceDirectory", pathError),
                "The captured source directory path is invalid.");
        }

        var sourceDirectoryInspection = await _probe
            .InspectAsync(sourceDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (!IsUsableDirectory(sourceDirectoryInspection, out var sourceDirectoryMessage))
        {
            return BlockPlan(
                plan,
                sourceDirectoryInspection,
                $"Source directory is not safe for delete preflight: {sourceDirectoryMessage}");
        }

        var items = new List<FileDeleteOperationPreflightItem>(plan.Intent.Entries.Count);
        foreach (var entry in plan.Intent.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ValidateEntryAsync(
                entry,
                sourceDirectory,
                cancellationToken).ConfigureAwait(false));
        }

        var blocked = 0;
        foreach (var item in items)
        {
            if (item.Decision == FileDeleteOperationPreflightDecision.Blocked)
            {
                blocked++;
            }
        }
        var status = blocked == 0
            ? FileDeleteOperationPreflightStatus.ReadyForFurtherReview
            : FileDeleteOperationPreflightStatus.Blocked;
        return new FileDeleteOperationPreflightResult(
            plan,
            sourceDirectoryInspection,
            items,
            status,
            $"Delete preflight: {items.Count - blocked} ready for further review, {blocked} blocked. " +
            "This is read-only evidence and is not authorization to delete, queue, or mutate any filesystem entry.");
    }

    private async ValueTask<FileDeleteOperationPreflightItem> ValidateEntryAsync(
        FileOperationEntry entry,
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!TryNormalize(entry.Path, out var sourcePath, out var pathError))
        {
            return Blocked(
                entry,
                ErrorInspection(entry.Path, "InvalidSourcePath", pathError),
                "The captured source entry path is invalid.");
        }

        var sourceParent = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceParent) || !PathsEqual(sourceParent, sourceDirectory))
        {
            return Blocked(
                entry,
                MissingInspection(sourcePath),
                "The source entry is no longer a direct child of the captured source directory.");
        }

        var leafName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(leafName) ||
            !string.Equals(leafName, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(
                entry,
                MissingInspection(sourcePath),
                "The captured source name no longer matches its path.");
        }
        if (leafName.Contains(Path.VolumeSeparatorChar))
        {
            return Blocked(
                entry,
                MissingInspection(sourcePath),
                "Alternate data stream paths are not supported by delete preflight.");
        }

        var sourceInspection = await _probe
            .InspectAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        if (sourceInspection.State == FileOperationPathState.Missing)
        {
            return Blocked(entry, sourceInspection, "The source entry no longer exists.");
        }
        if (sourceInspection.State is FileOperationPathState.Inaccessible or FileOperationPathState.Error)
        {
            return Blocked(entry, sourceInspection, "The source entry cannot be inspected safely.");
        }

        var expectedState = entry.IsDirectory
            ? FileOperationPathState.Directory
            : FileOperationPathState.File;
        if (sourceInspection.State != expectedState)
        {
            return Blocked(entry, sourceInspection, "The source entry type changed after capture.");
        }
        if (sourceInspection.IsReparsePoint)
        {
            return Blocked(
                entry,
                sourceInspection,
                "Reparse-point source entries require canonical target and recovery semantics before delete authorization can be considered.");
        }

        return new FileDeleteOperationPreflightItem(
            entry,
            sourceInspection,
            FileDeleteOperationPreflightDecision.ReadyForFurtherReview,
            "The captured source entry is still a direct, non-reparse child with the expected type. Further authorization and execution validation are still required before deletion can be considered.");
    }

    private static bool IsUsableDirectory(
        FileOperationPathInspection inspection,
        out string message)
    {
        if (inspection.State != FileOperationPathState.Directory)
        {
            message = inspection.State switch
            {
                FileOperationPathState.Missing => "the directory no longer exists",
                FileOperationPathState.Inaccessible => "access is denied",
                FileOperationPathState.File => "the path now refers to a file",
                _ => "the directory could not be inspected",
            };
            return false;
        }
        if (inspection.IsReparsePoint)
        {
            message = "the directory is a reparse point and canonical target safety is not defined for delete preflight";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static FileDeleteOperationPreflightResult BlockPlan(
        FileDeleteOperationPlan plan,
        FileOperationPathInspection sourceDirectory,
        string summary) =>
        new(
            plan,
            sourceDirectory,
            Array.Empty<FileDeleteOperationPreflightItem>(),
            FileDeleteOperationPreflightStatus.Blocked,
            summary + " No filesystem changes were attempted and deletion is not authorized.");

    private static FileDeleteOperationPreflightItem Blocked(
        FileOperationEntry entry,
        FileOperationPathInspection source,
        string message) =>
        new(entry, source, FileDeleteOperationPreflightDecision.Blocked, message);

    private static FileOperationPathInspection MissingInspection(string path) =>
        new(path, FileOperationPathState.Missing, IsReparsePoint: false);

    private static FileOperationPathInspection ErrorInspection(
        string path,
        string code,
        string message) =>
        new(path, FileOperationPathState.Error, IsReparsePoint: false, code, message);

    private static bool TryNormalize(
        string path,
        out string normalized,
        out string error)
    {
        try
        {
            normalized = Path.GetFullPath(path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = path;
            error = exception.Message;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForComparison(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == Path.VolumeSeparatorChar)
        {
            normalized += Path.DirectorySeparatorChar;
        }
        return normalized;
    }
}
