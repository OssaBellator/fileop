using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

/// <summary>
/// Renames one regular file within a volume using an identity-bound source handle and
/// a handle-relative destination name. Existing destinations are never replaced.
/// </summary>
public sealed class WindowsFileSameVolumeMoveMutationPrimitive : IFileSameVolumeMoveMutationPrimitive
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileTraverse = 0x0020;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;

    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const int FileRenameInfo = 3;

    public ValueTask<IFileSameVolumeMoveMutationLease> RenameFileAsync(
        FileSameVolumeMoveMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateInput(request);
        return new ValueTask<IFileSameVolumeMoveMutationLease>(
            Task.Run(() => RenameFile(request)));
    }

    private static IFileSameVolumeMoveMutationLease RenameFile(
        FileSameVolumeMoveMutationRequest request)
    {
        var item = request.Item;
        var sourceDirectoryIdentity = request.SourceDirectory.Identity
            ?? throw new InvalidOperationException("The validated Move source directory has no stable identity.");
        var destinationDirectoryIdentity = request.DestinationDirectory.Identity
            ?? throw new InvalidOperationException("The validated Move destination directory has no stable identity.");
        var expectedSourceIdentity = item.Source.Identity
            ?? throw new InvalidOperationException("The validated Move source file has no stable identity.");

        var sourceCanonicalPath = NormalizeForComparison(item.Source.CanonicalPath);
        var destinationCanonicalPath = NormalizeForComparison(item.Destination.CanonicalPath);
        var sourceParentPath = Path.GetDirectoryName(sourceCanonicalPath)
            ?? throw new InvalidOperationException("The validated Move source has no canonical parent.");
        var destinationParentPath = Path.GetDirectoryName(destinationCanonicalPath)
            ?? throw new InvalidOperationException("The validated Move destination has no canonical parent.");
        if (!PathsEqual(sourceParentPath, request.SourceDirectory.CanonicalPath) ||
            !PathsEqual(destinationParentPath, request.DestinationDirectory.CanonicalPath))
        {
            throw new InvalidOperationException(
                "The validated Move leaf parents no longer match the canonical source/destination roots.");
        }

        var sourceLeafName = Path.GetFileName(sourceCanonicalPath);
        var destinationLeafName = Path.GetFileName(destinationCanonicalPath);

        SafeFileHandle? sourceDirectory = null;
        SafeFileHandle? destinationDirectory = null;
        SafeFileHandle? sourceFile = null;
        try
        {
            sourceDirectory = OpenDirectoryHandle(request.SourceDirectory.CanonicalPath);
            ValidateDirectoryHandle(
                sourceDirectory,
                request.SourceDirectory.CanonicalPath,
                sourceDirectoryIdentity,
                "source");

            destinationDirectory = OpenDirectoryHandle(request.DestinationDirectory.CanonicalPath);
            ValidateDirectoryHandle(
                destinationDirectory,
                request.DestinationDirectory.CanonicalPath,
                destinationDirectoryIdentity,
                "destination");

            if (sourceDirectoryIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber ||
                expectedSourceIdentity.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber)
            {
                throw new IOException(
                    "The freshly opened Move roots/source are no longer identity-bound to one volume.");
            }

            sourceFile = OpenRelativeSourceFile(sourceDirectory, sourceLeafName);
            var sourceIdentity = ValidateSourceFileHandle(
                sourceFile,
                sourceCanonicalPath,
                expectedSourceIdentity);

            RenameRelative(
                sourceFile,
                destinationDirectory,
                destinationLeafName);

            // The source handle remains open across rename and must now resolve to the
            // exact destination while preserving the same file identity.
            var postRenameInformation = GetInformation(sourceFile, "renamed destination file");
            if ((postRenameInformation.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
                (postRenameInformation.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The renamed destination is no longer an ordinary file.");
            }

            var postRenamePath = GetFinalPath(sourceFile, "renamed destination file");
            if (!PathsEqual(postRenamePath, destinationCanonicalPath))
            {
                throw new IOException(
                    $"The renamed file resolved to an unexpected destination. Expected '{destinationCanonicalPath}', got '{postRenamePath}'.");
            }

            var destinationIdentity = ToIdentity(postRenameInformation);
            if (destinationIdentity != sourceIdentity)
            {
                throw new IOException(
                    "The same-volume rename did not preserve the validated source filesystem identity.");
            }

            // Recheck the destination parent identity after the namespace mutation so
            // the durable receipt cannot be attributed to a replaced parent directory.
            ValidateDirectoryHandle(
                destinationDirectory,
                request.DestinationDirectory.CanonicalPath,
                destinationDirectoryIdentity,
                "destination");

            var receipt = new FileSameVolumeMoveMutationReceipt(
                sourceCanonicalPath,
                destinationCanonicalPath,
                sourceIdentity,
                destinationIdentity);
            var lease = new MutationLease(
                receipt,
                sourceDirectory,
                destinationDirectory,
                sourceFile);
            sourceDirectory = null;
            destinationDirectory = null;
            sourceFile = null;
            return lease;
        }
        finally
        {
            DisposeNoThrow(sourceFile);
            DisposeNoThrow(destinationDirectory);
            DisposeNoThrow(sourceDirectory);
        }
    }

    private static void ValidateInput(FileSameVolumeMoveMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Item);
        ArgumentNullException.ThrowIfNull(request.SourceDirectory);
        ArgumentNullException.ThrowIfNull(request.DestinationDirectory);
        var item = request.Item;
        if (item.Entry.IsDirectory ||
            item.Decision != FileOperationExecutionValidationDecision.Ready ||
            item.Source.State != FileOperationCanonicalPathState.File ||
            item.Source.IsLeafReparsePoint ||
            !item.Source.Identity.HasValue ||
            item.Destination.State != FileOperationCanonicalPathState.Missing ||
            item.Destination.Identity.HasValue ||
            request.SourceDirectory.State != FileOperationCanonicalPathState.Directory ||
            request.DestinationDirectory.State != FileOperationCanonicalPathState.Directory ||
            request.SourceDirectory.IsLeafReparsePoint ||
            request.DestinationDirectory.IsLeafReparsePoint ||
            request.SourceDirectory.Identity is not FileIdentity sourceDirectoryIdentity ||
            request.DestinationDirectory.Identity is not FileIdentity destinationDirectoryIdentity ||
            sourceDirectoryIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber ||
            item.Source.Identity.Value.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Windows same-volume Move requires one freshly validated ready regular file and stable same-volume canonical roots.",
                nameof(request));
        }

        if (!IsLocalDrivePath(request.SourceDirectory.CanonicalPath) ||
            !IsLocalDrivePath(request.DestinationDirectory.CanonicalPath))
        {
            throw new ArgumentException(
                "The first same-volume Move primitive is restricted to local drive-letter paths; network/UNC Move remains unsupported.",
                nameof(request));
        }

        var sourceLeaf = Path.GetFileName(item.Source.CanonicalPath);
        var destinationLeaf = Path.GetFileName(item.Destination.CanonicalPath);
        if (string.IsNullOrWhiteSpace(sourceLeaf) ||
            string.IsNullOrWhiteSpace(destinationLeaf) ||
            !string.Equals(sourceLeaf, item.Entry.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(destinationLeaf, item.Entry.Name, StringComparison.OrdinalIgnoreCase) ||
            sourceLeaf.Contains(Path.VolumeSeparatorChar) ||
            destinationLeaf.Contains(Path.VolumeSeparatorChar) ||
            sourceLeaf.Contains(Path.DirectorySeparatorChar) ||
            destinationLeaf.Contains(Path.DirectorySeparatorChar) ||
            sourceLeaf.Contains(Path.AltDirectorySeparatorChar) ||
            destinationLeaf.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                "Windows same-volume Move requires one ordinary leaf name matching the immutable entry.",
                nameof(request));
        }
    }

    private static bool IsLocalDrivePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return root is { Length: >= 3 } &&
            root[1] == Path.VolumeSeparatorChar &&
            (root[2] == Path.DirectorySeparatorChar || root[2] == Path.AltDirectorySeparatorChar);
    }

    private static SafeFileHandle OpenDirectoryHandle(string canonicalPath)
    {
        var handle = CreateFileW(
            canonicalPath,
            FileTraverse | FileReadAttributes | Synchronize,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var exception = Win32IOException($"Opening Move directory '{canonicalPath}'");
            handle.Dispose();
            throw exception;
        }

        return handle;
    }

    private static void ValidateDirectoryHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity,
        string description)
    {
        var information = GetInformation(handle, description + " Move directory");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"The {description} Move parent changed into an unsafe directory object.");
        }

        var actualPath = GetFinalPath(handle, description + " Move directory");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The {description} Move parent path changed. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        if (ToIdentity(information) != expectedIdentity)
        {
            throw new IOException($"The {description} Move parent identity changed.");
        }
    }

    private static SafeFileHandle OpenRelativeSourceFile(
        SafeFileHandle sourceDirectory,
        string leafName)
    {
        if (string.IsNullOrWhiteSpace(leafName) ||
            leafName.Contains(Path.DirectorySeparatorChar) ||
            leafName.Contains(Path.AltDirectorySeparatorChar) ||
            leafName.Contains(Path.VolumeSeparatorChar))
        {
            throw new ArgumentException("A relative Move source leaf must contain one path component.", nameof(leafName));
        }

        var nameBuffer = Marshal.StringToHGlobalUni(leafName);
        var unicodeString = new UnicodeString
        {
            Length = checked((ushort)(leafName.Length * sizeof(char))),
            MaximumLength = checked((ushort)((leafName.Length + 1) * sizeof(char))),
            Buffer = nameBuffer,
        };
        var unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var rootAddedRef = false;
        try
        {
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            sourceDirectory.DangerousAddRef(ref rootAddedRef);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = sourceDirectory.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = ObjCaseInsensitive,
            };
            var status = NtCreateFile(
                out var rawHandle,
                DeleteAccess | FileReadAttributes | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                (uint)(FileShare.ReadWrite | FileShare.Delete),
                FileOpen,
                FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw new IOException(
                    $"NtCreateFile for Move source leaf '{leafName}' failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }

            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            {
                throw new IOException("NtCreateFile returned success without a valid Move source handle.");
            }

            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            if (rootAddedRef)
            {
                sourceDirectory.DangerousRelease();
            }

            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static FileIdentity ValidateSourceFileHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity)
    {
        var information = GetInformation(handle, "Move source file");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The validated Move source changed type before rename.");
        }

        var actualPath = GetFinalPath(handle, "Move source file");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The Move source canonical path changed. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        var identity = ToIdentity(information);
        if (identity != expectedIdentity)
        {
            throw new IOException("The validated Move source filesystem identity changed before rename.");
        }

        return identity;
    }

    private static void RenameRelative(
        SafeFileHandle sourceFile,
        SafeFileHandle destinationDirectory,
        string destinationLeafName)
    {
        var fileNameLength = checked(destinationLeafName.Length * sizeof(char));
        var fileNameOffset = checked((int)Marshal.OffsetOf<FileRenameInfoLayout>(nameof(FileRenameInfoLayout.FileName)));
        var bufferSize = checked(fileNameOffset + fileNameLength);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var destinationAddedRef = false;
        try
        {
            for (var offset = 0; offset < fileNameOffset; offset++)
            {
                Marshal.WriteByte(buffer, offset, 0);
            }

            destinationDirectory.DangerousAddRef(ref destinationAddedRef);
            Marshal.WriteInt32(
                buffer,
                checked((int)Marshal.OffsetOf<FileRenameInfoLayout>(nameof(FileRenameInfoLayout.FlagsOrReplace))),
                0); // ReplaceIfExists == FALSE.
            Marshal.WriteIntPtr(
                buffer,
                checked((int)Marshal.OffsetOf<FileRenameInfoLayout>(nameof(FileRenameInfoLayout.RootDirectory))),
                destinationDirectory.DangerousGetHandle());
            Marshal.WriteInt32(
                buffer,
                checked((int)Marshal.OffsetOf<FileRenameInfoLayout>(nameof(FileRenameInfoLayout.FileNameLength))),
                fileNameLength);
            Marshal.Copy(
                destinationLeafName.ToCharArray(),
                0,
                IntPtr.Add(buffer, fileNameOffset),
                destinationLeafName.Length);

            if (!SetFileInformationByHandle(
                    sourceFile,
                    FileRenameInfo,
                    buffer,
                    checked((uint)bufferSize)))
            {
                throw Win32IOException(
                    $"Renaming file to destination leaf '{destinationLeafName}' without replacement");
            }
        }
        finally
        {
            if (destinationAddedRef)
            {
                destinationDirectory.DangerousRelease();
            }

            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ByHandleFileInformation GetInformation(
        SafeFileHandle handle,
        string description)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw Win32IOException("Reading identity for " + description);
        }

        return information;
    }

    private static FileIdentity ToIdentity(ByHandleFileInformation information) =>
        new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

    private static string GetFinalPath(SafeFileHandle handle, string description)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw Win32IOException("Resolving final path for " + description);
            }

            if (length < buffer.Capacity)
            {
                return NormalizeFinalPath(buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }

        throw new IOException("Resolving the final Move handle path exceeded supported buffer growth attempts.");
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

    private static IOException Win32IOException(string action)
    {
        var error = Marshal.GetLastWin32Error();
        return new IOException($"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}");
    }

    private static void DisposeNoThrow(SafeFileHandle? handle)
    {
        if (handle is null)
        {
            return;
        }

        try
        {
            handle.Dispose();
        }
        catch
        {
        }
    }

    private sealed class MutationLease : IFileSameVolumeMoveMutationLease
    {
        private SafeFileHandle? _sourceDirectory;
        private SafeFileHandle? _destinationDirectory;
        private SafeFileHandle? _sourceFile;

        public MutationLease(
            FileSameVolumeMoveMutationReceipt receipt,
            SafeFileHandle sourceDirectory,
            SafeFileHandle destinationDirectory,
            SafeFileHandle sourceFile)
        {
            Receipt = receipt;
            _sourceDirectory = sourceDirectory;
            _destinationDirectory = destinationDirectory;
            _sourceFile = sourceFile;
        }

        public FileSameVolumeMoveMutationReceipt Receipt { get; }

        public ValueTask DisposeAsync()
        {
            DisposeNoThrow(_sourceFile);
            _sourceFile = null;
            DisposeNoThrow(_destinationDirectory);
            _destinationDirectory = null;
            DisposeNoThrow(_sourceDirectory);
            _sourceDirectory = null;
            return ValueTask.CompletedTask;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        IntPtr lpFileInformation,
        uint dwBufferSize);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInfoLayout
    {
        public uint FlagsOrReplace;
        public IntPtr RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

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
