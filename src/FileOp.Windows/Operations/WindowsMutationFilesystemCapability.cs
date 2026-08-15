using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

public enum WindowsMutationFilesystemCapabilityState
{
    SupportedNtfs,
    UnsupportedFilesystem,
    Unavailable,
}

/// <summary>
/// Filesystem evidence for one freshly validated mutation root. The current mutation
/// FileIdentity stores the 64-bit file index exposed by BY_HANDLE_FILE_INFORMATION, so the
/// beta mutation boundary is intentionally NTFS-only until a full-width identity model is
/// reviewed for filesystems such as ReFS.
/// </summary>
public sealed record WindowsMutationFilesystemCapability(
    string CanonicalDirectoryPath,
    FileIdentity ExpectedIdentity,
    WindowsMutationFilesystemCapabilityState State,
    string? FileSystemName,
    string Summary)
{
    public bool CanUseCurrentMutationIdentity =>
        State == WindowsMutationFilesystemCapabilityState.SupportedNtfs &&
        string.Equals(FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase);

    public bool IsBoundTo(string canonicalDirectoryPath, FileIdentity expectedIdentity) =>
        ExpectedIdentity == expectedIdentity &&
        string.Equals(
            CanonicalDirectoryPath,
            canonicalDirectoryPath,
            StringComparison.OrdinalIgnoreCase);
}

public interface IWindowsMutationFilesystemCapabilityProbe
{
    WindowsMutationFilesystemCapability QueryDirectory(
        string canonicalDirectoryPath,
        FileIdentity expectedIdentity);
}

/// <summary>
/// Proves the filesystem type through the same opened directory object whose path and
/// current FileIdentity are revalidated. This deliberately does not infer filesystem type
/// from a drive letter or a separately resolved root path.
/// </summary>
public sealed class WindowsMutationFilesystemCapabilityProbe :
    IWindowsMutationFilesystemCapabilityProbe
{
    private const uint FileFlagBackupSemantics = 0x02000000u;
    private const uint FileFlagOpenReparsePoint = 0x00200000u;
    private const int FileSystemNameCapacity = 261;

    public WindowsMutationFilesystemCapability QueryDirectory(
        string canonicalDirectoryPath,
        FileIdentity expectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDirectoryPath);

        string normalizedPath;
        try
        {
            normalizedPath = NormalizeForComparison(canonicalDirectoryPath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Unavailable(
                canonicalDirectoryPath,
                expectedIdentity,
                $"The mutation root cannot be normalized safely: {exception.Message}");
        }

        using var handle = CreateFileW(
            normalizedPath,
            dwDesiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Win32Unavailable(
                canonicalDirectoryPath,
                expectedIdentity,
                "Opening the exact mutation root",
                Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return Win32Unavailable(
                canonicalDirectoryPath,
                expectedIdentity,
                "Reading the exact mutation-root identity",
                Marshal.GetLastWin32Error());
        }

        if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            return Unavailable(
                canonicalDirectoryPath,
                expectedIdentity,
                "The exact mutation root changed type or became a reparse point before filesystem capability proof.");
        }

        var observedIdentity = ToIdentity(information);
        if (observedIdentity != expectedIdentity)
        {
            return Unavailable(
                canonicalDirectoryPath,
                expectedIdentity,
                $"The mutation-root filesystem identity changed before filesystem capability proof. Expected {expectedIdentity}, observed {observedIdentity}.");
        }

        var finalPath = TryGetFinalPath(handle, out var finalPathError);
        if (finalPath is null)
        {
            return Win32Unavailable(
                canonicalDirectoryPath,
                expectedIdentity,
                "Resolving the exact mutation-root final path",
                finalPathError);
        }
        if (!PathsEqual(finalPath, normalizedPath))
        {
            return Unavailable(
                canonicalDirectoryPath,
                expectedIdentity,
                $"The mutation-root canonical path changed before filesystem capability proof. Expected '{normalizedPath}', observed '{finalPath}'.");
        }

        var fileSystemName = new StringBuilder(FileSystemNameCapacity);
        if (!GetVolumeInformationByHandleW(
                handle,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                fileSystemName,
                checked((uint)fileSystemName.Capacity)))
        {
            return Win32Unavailable(
                canonicalDirectoryPath,
                expectedIdentity,
                "Reading the handle-bound filesystem type",
                Marshal.GetLastWin32Error());
        }

        var observedFileSystem = fileSystemName.ToString();
        if (string.Equals(observedFileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return new WindowsMutationFilesystemCapability(
                canonicalDirectoryPath,
                expectedIdentity,
                WindowsMutationFilesystemCapabilityState.SupportedNtfs,
                observedFileSystem,
                "The exact mutation root is identity-bound to NTFS, where the current 64-bit FileIdentity model is permitted for beta mutation execution.");
        }

        return new WindowsMutationFilesystemCapability(
            canonicalDirectoryPath,
            expectedIdentity,
            WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem,
            observedFileSystem,
            $"The exact mutation root uses filesystem '{observedFileSystem}'. The current mutation FileIdentity model is NTFS-only and fails closed on ReFS and other filesystems.");
    }

    private static WindowsMutationFilesystemCapability Win32Unavailable(
        string canonicalPath,
        FileIdentity expectedIdentity,
        string action,
        int error) =>
        Unavailable(
            canonicalPath,
            expectedIdentity,
            $"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}");

    private static WindowsMutationFilesystemCapability Unavailable(
        string canonicalPath,
        FileIdentity expectedIdentity,
        string summary) =>
        new(
            canonicalPath,
            expectedIdentity,
            WindowsMutationFilesystemCapabilityState.Unavailable,
            FileSystemName: null,
            summary);

    private static FileIdentity ToIdentity(ByHandleFileInformation information) =>
        new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

    private static string? TryGetFinalPath(SafeFileHandle handle, out int error)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                0);
            if (length == 0)
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }
            if (length < buffer.Capacity)
            {
                error = 0;
                return NormalizeFinalPath(buffer.ToString());
            }
            capacity = checked((int)length + 1);
        }

        error = 122; // ERROR_INSUFFICIENT_BUFFER
        return null;
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) &&
            path.Length >= 6 && path[5] == Path.VolumeSeparatorChar)
        {
            return path[4..];
        }
        return path;
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

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
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

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport(
        "kernel32.dll",
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle hFile,
        IntPtr lpVolumeNameBuffer,
        uint nVolumeNameSize,
        IntPtr lpVolumeSerialNumber,
        IntPtr lpMaximumComponentLength,
        IntPtr lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);
}

/// <summary>
/// Adds the beta NTFS-only mutation identity boundary to ordinary Copy/Move execution
/// validation. It is intentionally a decorator so browsing and non-mutating path resolution
/// remain filesystem-agnostic.
/// </summary>
public sealed class WindowsNtfsMutationExecutionValidator : IFileOperationExecutionValidator
{
    private readonly IFileOperationExecutionValidator _inner;
    private readonly IWindowsMutationFilesystemCapabilityProbe _probe;

    public WindowsNtfsMutationExecutionValidator(
        IFileOperationExecutionValidator? inner = null,
        IWindowsMutationFilesystemCapabilityProbe? probe = null)
    {
        _inner = inner ?? new WindowsFileOperationExecutionValidator();
        _probe = probe ?? new WindowsMutationFilesystemCapabilityProbe();
    }

    public async ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validation = await _inner.ValidateAsync(plan, cancellationToken).ConfigureAwait(false);
        if (plan.Kind is not (FileOperationKind.Copy or FileOperationKind.Move) ||
            !validation.CanBeginMutation)
        {
            return validation;
        }

        if (validation.SourceDirectory.Identity is not FileIdentity sourceIdentity ||
            validation.DestinationDirectory.Identity is not FileIdentity destinationIdentity)
        {
            return Block(
                validation,
                "Fresh execution validation did not provide stable source/destination root identity evidence required for the NTFS-only mutation boundary.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sourceCapability = _probe.QueryDirectory(
            validation.SourceDirectory.CanonicalPath,
            sourceIdentity);
        if (!CapabilityMatches(
                sourceCapability,
                validation.SourceDirectory.CanonicalPath,
                sourceIdentity))
        {
            return Block(
                validation,
                "Source mutation root filesystem capability was not exact NTFS evidence bound to the freshly validated root: " +
                sourceCapability.Summary);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var destinationCapability = _probe.QueryDirectory(
            validation.DestinationDirectory.CanonicalPath,
            destinationIdentity);
        if (!CapabilityMatches(
                destinationCapability,
                validation.DestinationDirectory.CanonicalPath,
                destinationIdentity))
        {
            return Block(
                validation,
                "Destination mutation root filesystem capability was not exact NTFS evidence bound to the freshly validated root: " +
                destinationCapability.Summary);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return validation;
    }

    private static bool CapabilityMatches(
        WindowsMutationFilesystemCapability capability,
        string canonicalDirectoryPath,
        FileIdentity expectedIdentity) =>
        capability is not null &&
        capability.CanUseCurrentMutationIdentity &&
        capability.IsBoundTo(canonicalDirectoryPath, expectedIdentity);

    private static FileOperationExecutionValidationResult Block(
        FileOperationExecutionValidationResult validation,
        string problem) =>
        new(
            validation.Plan,
            validation.SourceDirectory,
            validation.DestinationDirectory,
            validation.Items,
            FileOperationExecutionValidationStatus.Blocked,
            DateTimeOffset.UtcNow,
            "Mutation execution validation blocked the current filesystem identity model before durable mutation history: " +
            problem +
            " No MutationStarted record or filesystem mutation was created by this filesystem-capability refusal.");
}

/// <summary>
/// Delete equivalent of WindowsNtfsMutationExecutionValidator. The delete result contract
/// derives readiness from per-item decisions, so a root filesystem refusal explicitly turns
/// every otherwise-ready item into Blocked evidence before authorization review.
/// </summary>
public sealed class WindowsNtfsFileDeleteOperationExecutionValidator :
    IFileDeleteOperationExecutionValidator
{
    private readonly IFileDeleteOperationExecutionValidator _inner;
    private readonly IWindowsMutationFilesystemCapabilityProbe _probe;

    public WindowsNtfsFileDeleteOperationExecutionValidator(
        IFileDeleteOperationExecutionValidator? inner = null,
        IWindowsMutationFilesystemCapabilityProbe? probe = null)
    {
        _inner = inner ?? new WindowsFileDeleteOperationExecutionValidator();
        _probe = probe ?? new WindowsMutationFilesystemCapabilityProbe();
    }

    public async ValueTask<FileDeleteOperationExecutionValidationResult> ValidateAsync(
        FileDeleteOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validation = await _inner.ValidateAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!validation.CanRequestAuthorizationReview)
        {
            return validation;
        }

        if (validation.SourceDirectory.Identity is not FileIdentity sourceIdentity)
        {
            return Block(
                validation,
                "Fresh delete execution validation did not provide stable source-root identity evidence required for the NTFS-only mutation boundary.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capability = _probe.QueryDirectory(
            validation.SourceDirectory.CanonicalPath,
            sourceIdentity);
        if (capability is null ||
            !capability.CanUseCurrentMutationIdentity ||
            !capability.IsBoundTo(validation.SourceDirectory.CanonicalPath, sourceIdentity))
        {
            return Block(
                validation,
                "Delete source-root filesystem capability was not exact NTFS evidence bound to the freshly validated root: " +
                (capability?.Summary ?? "The filesystem capability provider returned no evidence."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return validation;
    }

    private static FileDeleteOperationExecutionValidationResult Block(
        FileDeleteOperationExecutionValidationResult validation,
        string problem)
    {
        var items = validation.Items
            .Select(item => item.Decision == FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview
                ? item with
                {
                    Decision = FileDeleteOperationExecutionValidationDecision.Blocked,
                    Message = problem + " No delete authorization was granted and no filesystem mutation was attempted.",
                }
                : item)
            .ToArray();

        return new FileDeleteOperationExecutionValidationResult(
            validation.Plan,
            validation.SourceDirectory,
            items,
            FileDeleteOperationExecutionValidationStatus.Blocked,
            DateTimeOffset.UtcNow,
            "Delete execution validation blocked the current filesystem identity model before authorization review: " +
            problem +
            " No delete authorization was granted and no filesystem mutation was attempted.");
    }
}
