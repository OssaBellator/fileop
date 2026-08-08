using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

public interface IFileCopyNamespaceBinder
{
    ValueTask<IFileCopyNamespaceBindingLease> BindAsync(
        FileCopyMutationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only identity binding for one freshly validated Copy-file request.
/// The handles stay open without delete sharing so the bound source file and
/// source/destination directory objects cannot be renamed or deleted while leased.
/// This lease does not authorize path-based destination creation.
/// </summary>
public interface IFileCopyNamespaceBindingLease : IAsyncDisposable
{
    SafeFileHandle SourceDirectoryHandle { get; }

    SafeFileHandle SourceFileHandle { get; }

    SafeFileHandle DestinationDirectoryHandle { get; }

    FileIdentity SourceDirectoryIdentity { get; }

    FileIdentity SourceFileIdentity { get; }

    FileIdentity DestinationDirectoryIdentity { get; }
}

public sealed class WindowsFileCopyNamespaceBinder : IFileCopyNamespaceBinder
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    public ValueTask<IFileCopyNamespaceBindingLease> BindAsync(
        FileCopyMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDirectChildPaths(request);

        SafeFileHandle? sourceDirectory = null;
        SafeFileHandle? destinationDirectory = null;
        SafeFileHandle? sourceFile = null;
        try
        {
            sourceDirectory = OpenExistingPath(
                request.SourceDirectory.CanonicalPath,
                desiredAccess: 0,
                shareMode: FileShareRead | FileShareWrite,
                flags: FileFlagBackupSemantics | FileFlagOpenReparsePoint);
            var sourceDirectoryInformation = Inspect(
                sourceDirectory,
                request.SourceDirectory.CanonicalPath,
                expectDirectory: true);
            EnsureIdentity(
                request.SourceDirectory.Identity!.Value,
                sourceDirectoryInformation.Identity,
                request.SourceDirectory.CanonicalPath,
                "source directory");

            cancellationToken.ThrowIfCancellationRequested();
            destinationDirectory = OpenExistingPath(
                request.DestinationDirectory.CanonicalPath,
                desiredAccess: 0,
                shareMode: FileShareRead | FileShareWrite,
                flags: FileFlagBackupSemantics | FileFlagOpenReparsePoint);
            var destinationDirectoryInformation = Inspect(
                destinationDirectory,
                request.DestinationDirectory.CanonicalPath,
                expectDirectory: true);
            EnsureIdentity(
                request.DestinationDirectory.Identity!.Value,
                destinationDirectoryInformation.Identity,
                request.DestinationDirectory.CanonicalPath,
                "destination directory");

            cancellationToken.ThrowIfCancellationRequested();
            sourceFile = OpenExistingPath(
                request.Item.Source.CanonicalPath,
                desiredAccess: GenericRead,
                shareMode: FileShareRead,
                flags: FileFlagOpenReparsePoint);
            var sourceFileInformation = Inspect(
                sourceFile,
                request.Item.Source.CanonicalPath,
                expectDirectory: false);
            EnsureIdentity(
                request.Item.Source.Identity!.Value,
                sourceFileInformation.Identity,
                request.Item.Source.CanonicalPath,
                "source file");

            cancellationToken.ThrowIfCancellationRequested();
            IFileCopyNamespaceBindingLease lease = new WindowsFileCopyNamespaceBindingLease(
                sourceDirectory,
                sourceFile,
                destinationDirectory,
                sourceDirectoryInformation.Identity,
                sourceFileInformation.Identity,
                destinationDirectoryInformation.Identity);
            sourceDirectory = null;
            sourceFile = null;
            destinationDirectory = null;
            return ValueTask.FromResult(lease);
        }
        finally
        {
            sourceFile?.Dispose();
            destinationDirectory?.Dispose();
            sourceDirectory?.Dispose();
        }
    }

    private static SafeFileHandle OpenExistingPath(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint flags)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException(
            $"Could not open the validated path for identity binding: {path}",
            new Win32Exception(error));
    }

    private static HandleInformation Inspect(
        SafeFileHandle handle,
        string path,
        bool expectDirectory)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Could not inspect the identity-bound path: {path}",
                new Win32Exception(error));
        }

        var attributes = (FileAttributes)information.FileAttributes;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new IOException(
                expectDirectory
                    ? $"The validated directory path now refers to a file: {path}"
                    : $"The validated source file path now refers to a directory: {path}");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"The identity-bound path is a reparse point and cannot enter the Copy mutation boundary: {path}");
        }

        var fileReference =
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow;
        return new HandleInformation(
            new FileIdentity(information.VolumeSerialNumber, fileReference));
    }

    private static void EnsureIdentity(
        FileIdentity expected,
        FileIdentity actual,
        string path,
        string description)
    {
        if (expected != actual)
        {
            throw new IOException(
                $"The {description} identity changed after canonical validation: {path}");
        }
    }

    private static void ValidateDirectChildPaths(FileCopyMutationRequest request)
    {
        var sourceParent = Path.GetDirectoryName(request.Item.Source.CanonicalPath);
        var destinationParent = Path.GetDirectoryName(request.Item.Destination.CanonicalPath);
        if (string.IsNullOrWhiteSpace(sourceParent) ||
            !PathsEqual(sourceParent, request.SourceDirectory.CanonicalPath))
        {
            throw new ArgumentException(
                "The validated source file is not a direct child of the bound source directory.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(destinationParent) ||
            !PathsEqual(destinationParent, request.DestinationDirectory.CanonicalPath))
        {
            throw new ArgumentException(
                "The validated destination leaf is not a direct child of the bound destination directory.",
                nameof(request));
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizePath(left),
            NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == Path.VolumeSeparatorChar)
        {
            normalized += Path.DirectorySeparatorChar;
        }

        return normalized;
    }

    private readonly record struct HandleInformation(FileIdentity Identity);

    private sealed class WindowsFileCopyNamespaceBindingLease : IFileCopyNamespaceBindingLease
    {
        private int _disposed;

        public WindowsFileCopyNamespaceBindingLease(
            SafeFileHandle sourceDirectoryHandle,
            SafeFileHandle sourceFileHandle,
            SafeFileHandle destinationDirectoryHandle,
            FileIdentity sourceDirectoryIdentity,
            FileIdentity sourceFileIdentity,
            FileIdentity destinationDirectoryIdentity)
        {
            SourceDirectoryHandle = sourceDirectoryHandle;
            SourceFileHandle = sourceFileHandle;
            DestinationDirectoryHandle = destinationDirectoryHandle;
            SourceDirectoryIdentity = sourceDirectoryIdentity;
            SourceFileIdentity = sourceFileIdentity;
            DestinationDirectoryIdentity = destinationDirectoryIdentity;
        }

        public SafeFileHandle SourceDirectoryHandle { get; }

        public SafeFileHandle SourceFileHandle { get; }

        public SafeFileHandle DestinationDirectoryHandle { get; }

        public FileIdentity SourceDirectoryIdentity { get; }

        public FileIdentity SourceFileIdentity { get; }

        public FileIdentity DestinationDirectoryIdentity { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                SourceFileHandle.Dispose();
                DestinationDirectoryHandle.Dispose();
                SourceDirectoryHandle.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}
