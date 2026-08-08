using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

public sealed class WindowsFileOperationPathProbe : IFileOperationPathProbe
{
    public async ValueTask<FileOperationPathInspection> InspectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
            () => Inspect(path, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static FileOperationPathInspection Inspect(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error(path, "InvalidPath", exception.Message);
        }

        try
        {
            var attributes = File.GetAttributes(normalized);
            return new FileOperationPathInspection(
                normalized,
                (attributes & FileAttributes.Directory) != 0
                    ? FileOperationPathState.Directory
                    : FileOperationPathState.File,
                IsReparsePoint: (attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch (FileNotFoundException)
        {
            return new FileOperationPathInspection(
                normalized,
                FileOperationPathState.Missing,
                IsReparsePoint: false);
        }
        catch (DirectoryNotFoundException)
        {
            return new FileOperationPathInspection(
                normalized,
                FileOperationPathState.Missing,
                IsReparsePoint: false);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new FileOperationPathInspection(
                normalized,
                FileOperationPathState.Inaccessible,
                IsReparsePoint: false,
                ErrorCode: "AccessDenied",
                ErrorMessage: exception.Message);
        }
        catch (SecurityException exception)
        {
            return new FileOperationPathInspection(
                normalized,
                FileOperationPathState.Inaccessible,
                IsReparsePoint: false,
                ErrorCode: "AccessDenied",
                ErrorMessage: exception.Message);
        }
        catch (IOException exception)
        {
            return Error(normalized, "IoError", exception.Message);
        }
    }

    private static FileOperationPathInspection Error(
        string path,
        string code,
        string message) =>
        new(
            path,
            FileOperationPathState.Error,
            IsReparsePoint: false,
            ErrorCode: code,
            ErrorMessage: message);
}

public sealed class WindowsFileOperationPreflightValidator : IFileOperationPreflightValidator
{
    private readonly IFileOperationPathProbe _probe;

    public WindowsFileOperationPreflightValidator(IFileOperationPathProbe? probe = null)
    {
        _probe = probe ?? new WindowsFileOperationPathProbe();
    }

    public async ValueTask<FileOperationPreflightResult> ValidateAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Intent);
        ArgumentNullException.ThrowIfNull(plan.Intent.Entries);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryNormalize(plan.Intent.SourceDirectoryPath, out var sourceDirectory, out var sourcePathError))
        {
            return BlockPlan(
                plan,
                ErrorInspection(plan.Intent.SourceDirectoryPath, "InvalidSourceDirectory", sourcePathError),
                MissingInspection(plan.Intent.DestinationDirectoryPath),
                "The captured source directory path is invalid.");
        }

        if (!TryNormalize(plan.Intent.DestinationDirectoryPath, out var destinationDirectory, out var destinationPathError))
        {
            return BlockPlan(
                plan,
                MissingInspection(sourceDirectory),
                ErrorInspection(plan.Intent.DestinationDirectoryPath, "InvalidDestinationDirectory", destinationPathError),
                "The captured destination directory path is invalid.");
        }

        if (PathsEqual(sourceDirectory, destinationDirectory))
        {
            return BlockPlan(
                plan,
                MissingInspection(sourceDirectory),
                MissingInspection(destinationDirectory),
                "Source and destination directories are the same.");
        }

        var sourceDirectoryInspection = await _probe
            .InspectAsync(sourceDirectory, cancellationToken)
            .ConfigureAwait(false);
        var destinationDirectoryInspection = await _probe
            .InspectAsync(destinationDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (!IsUsableDirectory(sourceDirectoryInspection, out var sourceDirectoryMessage))
        {
            return BlockPlan(
                plan,
                sourceDirectoryInspection,
                destinationDirectoryInspection,
                $"Source directory is not safe for preflight: {sourceDirectoryMessage}");
        }

        if (!IsUsableDirectory(destinationDirectoryInspection, out var destinationDirectoryMessage))
        {
            return BlockPlan(
                plan,
                sourceDirectoryInspection,
                destinationDirectoryInspection,
                $"Destination directory is not safe for preflight: {destinationDirectoryMessage}");
        }

        if (plan.Intent.Entries.Count == 0)
        {
            return BlockPlan(
                plan,
                sourceDirectoryInspection,
                destinationDirectoryInspection,
                "The operation plan contains no entries.");
        }

        var items = new List<FileOperationPreflightItem>(plan.Intent.Entries.Count);
        foreach (var entry in plan.Intent.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await ValidateEntryAsync(
                plan,
                entry,
                sourceDirectory,
                destinationDirectory,
                cancellationToken).ConfigureAwait(false));
        }

        var status = FileOperationPreflightStatus.Ready;
        foreach (var item in items)
        {
            if (item.Decision == FileOperationPreflightDecision.Blocked)
            {
                status = FileOperationPreflightStatus.Blocked;
                break;
            }

            if (item.Decision == FileOperationPreflightDecision.NeedsDecision)
            {
                status = FileOperationPreflightStatus.NeedsDecision;
            }
        }

        var ready = 0;
        var skipped = 0;
        var unresolved = 0;
        var blocked = 0;
        foreach (var item in items)
        {
            switch (item.Decision)
            {
                case FileOperationPreflightDecision.Ready:
                    ready++;
                    break;
                case FileOperationPreflightDecision.Skip:
                    skipped++;
                    break;
                case FileOperationPreflightDecision.NeedsDecision:
                    unresolved++;
                    break;
                case FileOperationPreflightDecision.Blocked:
                    blocked++;
                    break;
            }
        }

        return new FileOperationPreflightResult(
            plan,
            sourceDirectoryInspection,
            destinationDirectoryInspection,
            items.AsReadOnly(),
            status,
            $"Preflight: {ready} ready, {skipped} skipped, {unresolved} need a collision decision, {blocked} blocked. " +
            "This is read-only preflight and is not authorization to mutate the filesystem.");
    }

    private async ValueTask<FileOperationPreflightItem> ValidateEntryAsync(
        FileOperationPlan plan,
        FileOperationEntry entry,
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(entry.Path, out var sourcePath, out var pathError))
        {
            var invalid = ErrorInspection(entry.Path, "InvalidSourcePath", pathError);
            return Blocked(entry, destinationDirectory, invalid, MissingInspection(destinationDirectory),
                "The captured source entry path is invalid.");
        }

        var sourceParent = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceParent) || !PathsEqual(sourceParent, sourceDirectory))
        {
            return Blocked(
                entry,
                destinationDirectory,
                MissingInspection(sourcePath),
                MissingInspection(destinationDirectory),
                "The source entry is no longer a direct child of the captured source directory.");
        }

        var leafName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(leafName) ||
            !string.Equals(leafName, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(
                entry,
                destinationDirectory,
                MissingInspection(sourcePath),
                MissingInspection(destinationDirectory),
                "The captured source name no longer matches its path.");
        }

        if (leafName.Contains(Path.VolumeSeparatorChar))
        {
            return Blocked(
                entry,
                destinationDirectory,
                MissingInspection(sourcePath),
                MissingInspection(destinationDirectory),
                "Alternate data stream paths are not supported by the file-operation preflight boundary.");
        }

        if (entry.IsDirectory && IsSameOrDescendantPath(destinationDirectory, sourcePath))
        {
            return Blocked(
                entry,
                destinationDirectory,
                MissingInspection(sourcePath),
                MissingInspection(destinationDirectory),
                "A directory cannot target itself or one of its descendants.");
        }

        var destinationPath = Path.Combine(destinationDirectory, leafName);
        var sourceInspection = await _probe
            .InspectAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);

        if (sourceInspection.State == FileOperationPathState.Missing)
        {
            return Blocked(entry, destinationPath, sourceInspection, MissingInspection(destinationPath),
                "The source entry no longer exists.");
        }

        if (sourceInspection.State is FileOperationPathState.Inaccessible or FileOperationPathState.Error)
        {
            return Blocked(entry, destinationPath, sourceInspection, MissingInspection(destinationPath),
                "The source entry cannot be inspected safely.");
        }

        var expectedSourceState = entry.IsDirectory
            ? FileOperationPathState.Directory
            : FileOperationPathState.File;
        if (sourceInspection.State != expectedSourceState)
        {
            return Blocked(entry, destinationPath, sourceInspection, MissingInspection(destinationPath),
                "The source entry type changed after the plan was captured.");
        }

        if (sourceInspection.IsReparsePoint)
        {
            return Blocked(entry, destinationPath, sourceInspection, MissingInspection(destinationPath),
                "Reparse-point source entries require canonical target semantics before mutation can be considered.");
        }

        var destinationInspection = await _probe
            .InspectAsync(destinationPath, cancellationToken)
            .ConfigureAwait(false);

        return destinationInspection.State switch
        {
            FileOperationPathState.Missing => new FileOperationPreflightItem(
                entry,
                destinationPath,
                sourceInspection,
                destinationInspection,
                FileOperationPreflightDecision.Ready,
                "No destination collision is currently visible."),
            FileOperationPathState.File or FileOperationPathState.Directory =>
                ClassifyCollision(plan.CollisionPolicy, entry, destinationPath, sourceInspection, destinationInspection),
            FileOperationPathState.Inaccessible => Blocked(
                entry,
                destinationPath,
                sourceInspection,
                destinationInspection,
                "The destination path exists or is protected but cannot be inspected safely."),
            _ => Blocked(
                entry,
                destinationPath,
                sourceInspection,
                destinationInspection,
                "The destination path could not be inspected safely."),
        };
    }

    private static FileOperationPreflightItem ClassifyCollision(
        FileOperationCollisionPolicy collisionPolicy,
        FileOperationEntry entry,
        string destinationPath,
        FileOperationPathInspection source,
        FileOperationPathInspection destination) =>
        collisionPolicy switch
        {
            FileOperationCollisionPolicy.Ask => new FileOperationPreflightItem(
                entry,
                destinationPath,
                source,
                destination,
                FileOperationPreflightDecision.NeedsDecision,
                "The destination already exists; an explicit collision decision is required."),
            FileOperationCollisionPolicy.Skip => new FileOperationPreflightItem(
                entry,
                destinationPath,
                source,
                destination,
                FileOperationPreflightDecision.Skip,
                "The destination already exists and this item is planned to be skipped."),
            FileOperationCollisionPolicy.Stop => Blocked(
                entry,
                destinationPath,
                source,
                destination,
                "The destination already exists and the plan is configured to stop on collision."),
            _ => Blocked(
                entry,
                destinationPath,
                source,
                destination,
                "The collision policy is not recognized."),
        };

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
            message = "the directory is a reparse point and canonical target safety is not defined yet";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static FileOperationPreflightResult BlockPlan(
        FileOperationPlan plan,
        FileOperationPathInspection sourceDirectory,
        FileOperationPathInspection destinationDirectory,
        string summary) =>
        new(
            plan,
            sourceDirectory,
            destinationDirectory,
            Array.Empty<FileOperationPreflightItem>(),
            FileOperationPreflightStatus.Blocked,
            summary + " No filesystem changes were attempted.");

    private static FileOperationPreflightItem Blocked(
        FileOperationEntry entry,
        string destinationPath,
        FileOperationPathInspection source,
        FileOperationPathInspection destination,
        string message) =>
        new(
            entry,
            destinationPath,
            source,
            destination,
            FileOperationPreflightDecision.Blocked,
            message);

    private static FileOperationPathInspection MissingInspection(string path) =>
        new(path, FileOperationPathState.Missing, IsReparsePoint: false);

    private static FileOperationPathInspection ErrorInspection(
        string path,
        string code,
        string message) =>
        new(
            path,
            FileOperationPathState.Error,
            IsReparsePoint: false,
            ErrorCode: code,
            ErrorMessage: message);

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

    private static bool IsSameOrDescendantPath(string candidate, string root)
    {
        var normalizedCandidate = NormalizeForComparison(candidate);
        var normalizedRoot = NormalizeForComparison(root);
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
            normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

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
