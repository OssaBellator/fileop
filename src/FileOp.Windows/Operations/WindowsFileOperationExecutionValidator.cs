using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

public sealed class WindowsFileOperationCanonicalPathResolver : IFileOperationCanonicalPathResolver
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    public ValueTask<FileOperationCanonicalPath> ResolveAsync(
        string path,
        bool allowMissingLeaf = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<FileOperationCanonicalPath>(
            Task.Run(
                () => Resolve(path, allowMissingLeaf, cancellationToken),
                cancellationToken));
    }

    private static FileOperationCanonicalPath Resolve(
        string path,
        bool allowMissingLeaf,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string requestedPath;
        try
        {
            requestedPath = Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error(path, path, "InvalidPath", exception.Message);
        }

        var leafIsReparsePoint = false;
        try
        {
            var attributes = File.GetAttributes(requestedPath);
            leafIsReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return allowMissingLeaf
                ? ResolveMissingLeaf(requestedPath, cancellationToken)
                : Missing(requestedPath);
        }
        catch (DirectoryNotFoundException)
        {
            return allowMissingLeaf
                ? ResolveMissingLeaf(requestedPath, cancellationToken)
                : Missing(requestedPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Inaccessible(requestedPath, exception.Message);
        }
        catch (SecurityException exception)
        {
            return Inaccessible(requestedPath, exception.Message);
        }
        catch (IOException exception)
        {
            return Error(requestedPath, requestedPath, "IoError", exception.Message);
        }

        using var handle = CreateFileW(
            requestedPath,
            dwDesiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return FromWin32Failure(requestedPath, Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return FromWin32Failure(requestedPath, Marshal.GetLastWin32Error());
        }

        var finalPath = TryGetFinalPath(handle, out var finalPathError);
        if (finalPath is null)
        {
            return FromWin32Failure(requestedPath, finalPathError);
        }

        var state = (information.FileAttributes & (uint)FileAttributes.Directory) != 0
            ? FileOperationCanonicalPathState.Directory
            : FileOperationCanonicalPathState.File;
        var identity = new FileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

        return new FileOperationCanonicalPath(
            requestedPath,
            NormalizeFinalPath(finalPath),
            state,
            leafIsReparsePoint,
            identity);
    }

    private static FileOperationCanonicalPath ResolveMissingLeaf(
        string requestedPath,
        CancellationToken cancellationToken)
    {
        var leafName = Path.GetFileName(requestedPath);
        var parentPath = Path.GetDirectoryName(requestedPath);
        if (string.IsNullOrWhiteSpace(leafName) || string.IsNullOrWhiteSpace(parentPath))
        {
            return Error(
                requestedPath,
                requestedPath,
                "InvalidMissingLeaf",
                "A missing operation target must have an existing parent directory and a leaf name.");
        }

        var parent = Resolve(parentPath, allowMissingLeaf: false, cancellationToken);
        if (parent.State == FileOperationCanonicalPathState.Inaccessible)
        {
            return new FileOperationCanonicalPath(
                requestedPath,
                requestedPath,
                FileOperationCanonicalPathState.Inaccessible,
                IsLeafReparsePoint: false,
                ErrorCode: parent.ErrorCode,
                ErrorMessage: parent.ErrorMessage);
        }

        if (parent.State != FileOperationCanonicalPathState.Directory)
        {
            return Error(
                requestedPath,
                requestedPath,
                "MissingParent",
                "The parent directory for the missing operation target could not be resolved.");
        }

        return new FileOperationCanonicalPath(
            requestedPath,
            Path.Combine(parent.CanonicalPath, leafName),
            FileOperationCanonicalPathState.Missing,
            IsLeafReparsePoint: false);
    }

    private static string? TryGetFinalPath(SafeFileHandle handle, out int error)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }

            if (length < buffer.Capacity)
            {
                error = 0;
                return buffer.ToString();
            }

            capacity = checked((int)length + 1);
        }

        error = 0;
        return null;
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return path[4..];
        }

        return path;
    }

    private static FileOperationCanonicalPath FromWin32Failure(string path, int error)
    {
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return Missing(path);
        }

        if (error == ErrorAccessDenied)
        {
            return new FileOperationCanonicalPath(
                path,
                path,
                FileOperationCanonicalPathState.Inaccessible,
                IsLeafReparsePoint: false,
                ErrorCode: "AccessDenied",
                ErrorMessage: $"Windows path resolution failed with error {error}.");
        }

        return Error(
            path,
            path,
            $"Win32:{error}",
            $"Windows path resolution failed with error {error}.");
    }

    private static FileOperationCanonicalPath Missing(string path) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Missing,
            IsLeafReparsePoint: false);

    private static FileOperationCanonicalPath Inaccessible(string path, string message) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Inaccessible,
            IsLeafReparsePoint: false,
            ErrorCode: "AccessDenied",
            ErrorMessage: message);

    private static FileOperationCanonicalPath Error(
        string requestedPath,
        string canonicalPath,
        string code,
        string message) =>
        new(
            requestedPath,
            canonicalPath,
            FileOperationCanonicalPathState.Error,
            IsLeafReparsePoint: false,
            ErrorCode: code,
            ErrorMessage: message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}

public sealed class WindowsFileOperationExecutionValidator : IFileOperationExecutionValidator
{
    private readonly IFileOperationCanonicalPathResolver _resolver;

    public WindowsFileOperationExecutionValidator(IFileOperationCanonicalPathResolver? resolver = null)
    {
        _resolver = resolver ?? new WindowsFileOperationCanonicalPathResolver();
    }

    public async ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Intent);
        ArgumentNullException.ThrowIfNull(plan.Intent.Entries);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceDirectory = await _resolver
            .ResolveAsync(plan.Intent.SourceDirectoryPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var destinationDirectory = await _resolver
            .ResolveAsync(plan.Intent.DestinationDirectoryPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var rootProblem = ValidateRootPair(sourceDirectory, destinationDirectory);
        if (rootProblem is not null)
        {
            return BlockPlan(plan, sourceDirectory, destinationDirectory, rootProblem);
        }

        if (plan.Intent.Entries.Count == 0)
        {
            return BlockPlan(
                plan,
                sourceDirectory,
                destinationDirectory,
                "The operation plan contains no entries.");
        }

        var items = new List<FileOperationExecutionValidationItem>(plan.Intent.Entries.Count);
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

        var status = FileOperationExecutionValidationStatus.Ready;
        foreach (var item in items)
        {
            if (item.Decision == FileOperationExecutionValidationDecision.Blocked)
            {
                status = FileOperationExecutionValidationStatus.Blocked;
                break;
            }

            if (item.Decision == FileOperationExecutionValidationDecision.NeedsDecision)
            {
                status = FileOperationExecutionValidationStatus.NeedsDecision;
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
                case FileOperationExecutionValidationDecision.Ready:
                    ready++;
                    break;
                case FileOperationExecutionValidationDecision.Skip:
                    skipped++;
                    break;
                case FileOperationExecutionValidationDecision.NeedsDecision:
                    unresolved++;
                    break;
                case FileOperationExecutionValidationDecision.Blocked:
                    blocked++;
                    break;
            }
        }

        return new FileOperationExecutionValidationResult(
            plan,
            sourceDirectory,
            destinationDirectory,
            items,
            status,
            DateTimeOffset.UtcNow,
            $"Execution validation: {ready} ready, {skipped} skipped, {unresolved} need a collision decision, {blocked} blocked. " +
            "All path relationships were checked against handle-resolved final paths; no filesystem mutation was attempted.");
    }

    private async ValueTask<FileOperationExecutionValidationItem> ValidateEntryAsync(
        FileOperationPlan plan,
        FileOperationEntry entry,
        FileOperationCanonicalPath sourceDirectory,
        FileOperationCanonicalPath destinationDirectory,
        CancellationToken cancellationToken)
    {
        string requestedSource;
        try
        {
            requestedSource = Path.GetFullPath(entry.Path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Blocked(
                entry,
                Error(entry.Path, "InvalidSourcePath", exception.Message),
                Missing(Path.Combine(destinationDirectory.CanonicalPath, entry.Name)),
                "The captured source entry path is invalid.");
        }

        var requestedParent = Path.GetDirectoryName(requestedSource);
        if (string.IsNullOrWhiteSpace(requestedParent) ||
            !PathsEqual(requestedParent, plan.Intent.SourceDirectoryPath))
        {
            return Blocked(
                entry,
                Missing(requestedSource),
                Missing(Path.Combine(destinationDirectory.CanonicalPath, entry.Name)),
                "The captured source entry is not a direct child of the captured source directory.");
        }

        var leafName = Path.GetFileName(requestedSource);
        if (string.IsNullOrWhiteSpace(leafName) ||
            !string.Equals(leafName, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(
                entry,
                Missing(requestedSource),
                Missing(Path.Combine(destinationDirectory.CanonicalPath, entry.Name)),
                "The captured source name no longer matches its path.");
        }

        if (leafName.Contains(Path.VolumeSeparatorChar))
        {
            return Blocked(
                entry,
                Missing(requestedSource),
                Missing(Path.Combine(destinationDirectory.CanonicalPath, leafName)),
                "Alternate data stream names are not supported by the execution validation boundary.");
        }

        var requestedDestination = Path.Combine(plan.Intent.DestinationDirectoryPath, leafName);
        var source = await _resolver
            .ResolveAsync(requestedSource, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var destination = await _resolver
            .ResolveAsync(
                requestedDestination,
                allowMissingLeaf: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (source.State is FileOperationCanonicalPathState.Inaccessible or FileOperationCanonicalPathState.Error)
        {
            return Blocked(entry, source, destination, "The source entry cannot be resolved safely.");
        }

        var expectedSourceState = entry.IsDirectory
            ? FileOperationCanonicalPathState.Directory
            : FileOperationCanonicalPathState.File;
        if (source.State != expectedSourceState)
        {
            return Blocked(entry, source, destination, "The source entry is missing or its type changed.");
        }

        if (source.IsLeafReparsePoint)
        {
            return Blocked(
                entry,
                source,
                destination,
                "A source entry that is itself a reparse point cannot be mutated by this execution boundary.");
        }

        var canonicalSourceParent = Path.GetDirectoryName(NormalizeForComparison(source.CanonicalPath));
        if (string.IsNullOrWhiteSpace(canonicalSourceParent) ||
            !PathsEqual(canonicalSourceParent, sourceDirectory.CanonicalPath))
        {
            return Blocked(
                entry,
                source,
                destination,
                "The source resolves outside the canonical source directory.");
        }

        if (destination.State is FileOperationCanonicalPathState.Inaccessible or FileOperationCanonicalPathState.Error)
        {
            return Blocked(entry, source, destination, "The destination cannot be resolved safely.");
        }

        if (destination.IsLeafReparsePoint)
        {
            return Blocked(
                entry,
                source,
                destination,
                "An existing destination that is itself a reparse point is not a safe mutation target.");
        }

        var canonicalDestinationParent = Path.GetDirectoryName(NormalizeForComparison(destination.CanonicalPath));
        if (string.IsNullOrWhiteSpace(canonicalDestinationParent) ||
            !PathsEqual(canonicalDestinationParent, destinationDirectory.CanonicalPath))
        {
            return Blocked(
                entry,
                source,
                destination,
                "The destination resolves outside the canonical destination directory.");
        }

        if (entry.IsDirectory &&
            IsSameOrDescendantPath(destinationDirectory.CanonicalPath, source.CanonicalPath))
        {
            return Blocked(
                entry,
                source,
                destination,
                "The canonical destination directory is the source directory or one of its descendants.");
        }

        if (source.Identity is FileIdentity sourceIdentity &&
            destination.Identity is FileIdentity destinationIdentity &&
            sourceIdentity == destinationIdentity)
        {
            return Blocked(
                entry,
                source,
                destination,
                "The source and destination resolve to the same filesystem object.");
        }

        return destination.State switch
        {
            FileOperationCanonicalPathState.Missing => Ready(
                entry,
                source,
                destination,
                "The destination leaf is absent under the resolved destination directory."),
            FileOperationCanonicalPathState.File or FileOperationCanonicalPathState.Directory =>
                ClassifyCollision(plan.CollisionPolicy, entry, source, destination),
            _ => Blocked(entry, source, destination, "The destination is not safe for mutation."),
        };
    }

    private static string? ValidateRootPair(
        FileOperationCanonicalPath sourceDirectory,
        FileOperationCanonicalPath destinationDirectory)
    {
        if (sourceDirectory.State != FileOperationCanonicalPathState.Directory)
        {
            return "The source directory is unavailable or does not resolve to a directory.";
        }

        if (destinationDirectory.State != FileOperationCanonicalPathState.Directory)
        {
            return "The destination directory is unavailable or does not resolve to a directory.";
        }

        if (sourceDirectory.IsLeafReparsePoint || destinationDirectory.IsLeafReparsePoint)
        {
            return "Captured source/destination roots that are themselves reparse points remain blocked.";
        }

        if (PathsEqual(sourceDirectory.CanonicalPath, destinationDirectory.CanonicalPath))
        {
            return "Source and destination resolve to the same canonical directory.";
        }

        if (sourceDirectory.Identity is FileIdentity sourceIdentity &&
            destinationDirectory.Identity is FileIdentity destinationIdentity &&
            sourceIdentity == destinationIdentity)
        {
            return "Source and destination resolve to the same directory identity.";
        }

        return null;
    }

    private static FileOperationExecutionValidationItem ClassifyCollision(
        FileOperationCollisionPolicy collisionPolicy,
        FileOperationEntry entry,
        FileOperationCanonicalPath source,
        FileOperationCanonicalPath destination) =>
        collisionPolicy switch
        {
            FileOperationCollisionPolicy.Ask => new FileOperationExecutionValidationItem(
                entry,
                source,
                destination,
                FileOperationExecutionValidationDecision.NeedsDecision,
                "The canonical destination already exists; an explicit collision decision is required."),
            FileOperationCollisionPolicy.Skip => new FileOperationExecutionValidationItem(
                entry,
                source,
                destination,
                FileOperationExecutionValidationDecision.Skip,
                "The canonical destination already exists and this item is planned to be skipped."),
            FileOperationCollisionPolicy.Stop => Blocked(
                entry,
                source,
                destination,
                "The canonical destination already exists and the plan is configured to stop."),
            _ => Blocked(entry, source, destination, "The collision policy is not recognized."),
        };

    private static FileOperationExecutionValidationItem Ready(
        FileOperationEntry entry,
        FileOperationCanonicalPath source,
        FileOperationCanonicalPath destination,
        string message) =>
        new(
            entry,
            source,
            destination,
            FileOperationExecutionValidationDecision.Ready,
            message);

    private static FileOperationExecutionValidationItem Blocked(
        FileOperationEntry entry,
        FileOperationCanonicalPath source,
        FileOperationCanonicalPath destination,
        string message) =>
        new(
            entry,
            source,
            destination,
            FileOperationExecutionValidationDecision.Blocked,
            message);

    private static FileOperationExecutionValidationResult BlockPlan(
        FileOperationPlan plan,
        FileOperationCanonicalPath sourceDirectory,
        FileOperationCanonicalPath destinationDirectory,
        string summary) =>
        new(
            plan,
            sourceDirectory,
            destinationDirectory,
            Array.Empty<FileOperationExecutionValidationItem>(),
            FileOperationExecutionValidationStatus.Blocked,
            DateTimeOffset.UtcNow,
            summary + " No filesystem mutation was attempted.");

    private static FileOperationCanonicalPath Missing(string path) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Missing,
            IsLeafReparsePoint: false);

    private static FileOperationCanonicalPath Error(string path, string code, string message) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Error,
            IsLeafReparsePoint: false,
            ErrorCode: code,
            ErrorMessage: message);

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
