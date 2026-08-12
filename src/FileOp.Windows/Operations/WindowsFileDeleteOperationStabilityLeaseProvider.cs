using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

/// <summary>
/// Acquires read-only root/direct-child handles for one explicitly authorized file.
/// The handles intentionally omit delete sharing so namespace mutation is blocked
/// while the lease lives, but the handles themselves request no DELETE access.
/// </summary>
public sealed class WindowsFileDeleteOperationStabilityLeaseProvider : IFileDeleteOperationStabilityLeaseProvider
{
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

    private readonly IFileDeleteProtectedLocationPolicy _protectedLocationPolicy;

    public WindowsFileDeleteOperationStabilityLeaseProvider(
        IFileDeleteProtectedLocationPolicy? protectedLocationPolicy = null)
    {
        _protectedLocationPolicy = protectedLocationPolicy ?? new WindowsFileDeleteProtectedLocationPolicy();
    }

    public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
        FileDeleteOperationStabilityLeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);

        return new ValueTask<IFileDeleteOperationStabilityLease>(
            Task.Run(() => Acquire(request), cancellationToken));
    }

    private IFileDeleteOperationStabilityLease Acquire(
        FileDeleteOperationStabilityLeaseRequest request)
    {
        var authorization = request.Authorization;
        var expectedRootPath = NormalizeForComparison(authorization.CanonicalSourceDirectoryPath);
        var expectedSourcePath = NormalizeForComparison(request.AuthorizedItem.CanonicalPath);
        var expectedRootIdentity = authorization.SourceDirectoryIdentity;
        var expectedSourceIdentity = request.AuthorizedItem.Identity;

        var sourceParent = Path.GetDirectoryName(expectedSourcePath)
            ?? throw new InvalidOperationException(
                "The authorized delete file has no canonical parent directory.");
        if (!PathsEqual(sourceParent, expectedRootPath))
        {
            throw new InvalidOperationException(
                "The authorized delete file is no longer represented as a direct child of the authorized canonical root.");
        }

        var leafName = Path.GetFileName(expectedSourcePath);
        if (string.IsNullOrWhiteSpace(leafName) ||
            !string.Equals(leafName, request.Entry.Name, StringComparison.OrdinalIgnoreCase) ||
            leafName.Contains(Path.DirectorySeparatorChar) ||
            leafName.Contains(Path.AltDirectorySeparatorChar) ||
            leafName.Contains(Path.VolumeSeparatorChar))
        {
            throw new InvalidOperationException(
                "The authorized delete leaf name is not a safe exact direct-child component.");
        }

        EnsureAllowedByProtectedLocationPolicy(expectedRootPath, "source directory");
        EnsureAllowedByProtectedLocationPolicy(expectedSourcePath, "source file");

        SafeFileHandle? sourceDirectory = null;
        SafeFileHandle? sourceFile = null;
        try
        {
            sourceDirectory = OpenDirectoryHandle(expectedRootPath);
            ValidateDirectoryHandle(
                sourceDirectory,
                expectedRootPath,
                expectedRootIdentity);

            sourceFile = OpenRelativeFile(
                sourceDirectory,
                leafName,
                FileReadAttributes | Synchronize,
                FileShare.Read,
                FileOpen,
                FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint);
            ValidateFileHandle(
                sourceFile,
                expectedSourcePath,
                expectedSourceIdentity);

            // Revalidate the held root after the relative leaf open so the final
            // evidence describes one stable root/direct-child handle set.
            ValidateDirectoryHandle(
                sourceDirectory,
                expectedRootPath,
                expectedRootIdentity);

            var evidence = new FileDeleteOperationStabilityLeaseEvidence(
                request,
                authorization.CanonicalSourceDirectoryPath,
                expectedRootIdentity,
                request.AuthorizedItem.CanonicalPath,
                expectedSourceIdentity);
            var lease = new StabilityLease(evidence, sourceDirectory, sourceFile);
            sourceDirectory = null;
            sourceFile = null;
            return lease;
        }
        finally
        {
            DisposeNoThrow(sourceFile);
            DisposeNoThrow(sourceDirectory);
        }
    }

    private static void ValidateRequest(FileDeleteOperationStabilityLeaseRequest request)
    {
        var authorization = request.Authorization;
        if (!authorization.UserAuthorizedAttempt ||
            authorization.DeleteMutationAuthorized ||
            request.DeleteMutationAuthorized ||
            request.Entry.IsDirectory ||
            request.Ordinal < 0 ||
            request.Ordinal >= authorization.Items.Count ||
            !ReferenceEquals(request.AuthorizedItem, authorization.Items[request.Ordinal]) ||
            request.AuthorizedItem.Entry != authorization.Plan.Intent.Entries[request.Ordinal] ||
            string.IsNullOrWhiteSpace(authorization.CanonicalSourceDirectoryPath) ||
            string.IsNullOrWhiteSpace(request.AuthorizedItem.CanonicalPath))
        {
            throw new ArgumentException(
                "Windows delete stability leasing requires one exact authorized, non-mutating file request.",
                nameof(request));
        }
    }

    private void EnsureAllowedByProtectedLocationPolicy(string canonicalPath, string description)
    {
        var decision = _protectedLocationPolicy.Evaluate(canonicalPath);
        if (decision.IsBlocked)
        {
            throw new UnauthorizedAccessException(
                $"The canonical delete {description} is protected: {decision.Reason}");
        }
    }

    private static SafeFileHandle OpenDirectoryHandle(string canonicalPath)
    {
        var handle = CreateFileW(
            canonicalPath,
            FileTraverse | FileReadAttributes | Synchronize,
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var exception = Win32IOException(
                $"Opening authorized canonical delete directory '{canonicalPath}'");
            handle.Dispose();
            throw exception;
        }

        return handle;
    }

    private static SafeFileHandle OpenRelativeFile(
        SafeFileHandle rootDirectory,
        string leafName,
        uint desiredAccess,
        FileShare shareAccess,
        uint createDisposition,
        uint createOptions)
    {
        if (string.IsNullOrWhiteSpace(leafName) ||
            leafName.Contains(Path.DirectorySeparatorChar) ||
            leafName.Contains(Path.AltDirectorySeparatorChar) ||
            leafName.Contains(Path.VolumeSeparatorChar))
        {
            throw new ArgumentException(
                "A relative delete stability leaf must contain only one path component.",
                nameof(leafName));
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
            rootDirectory.DangerousAddRef(ref rootAddedRef);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = rootDirectory.DangerousGetHandle(),
                ObjectName = unicodeStringPointer,
                Attributes = ObjCaseInsensitive,
            };
            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                (uint)shareAccess,
                createDisposition,
                createOptions,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw new IOException(
                    $"NtCreateFile for authorized relative delete leaf '{leafName}' failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }

            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            {
                throw new IOException(
                    "NtCreateFile returned success without a valid read-only delete stability handle.");
            }

            return new SafeFileHandle(rawHandle, ownsHandle: true);
        }
        finally
        {
            if (rootAddedRef)
            {
                rootDirectory.DangerousRelease();
            }

            Marshal.FreeHGlobal(unicodeStringPointer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static void ValidateDirectoryHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity)
    {
        var information = GetInformation(handle, "authorized delete source directory");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The authorized delete source directory changed into an unsafe object before stability leasing.");
        }

        var actualPath = GetFinalPath(handle, "authorized delete source directory");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The authorized delete source directory canonical path changed. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        if (ToIdentity(information) != expectedIdentity)
        {
            throw new IOException(
                "The authorized delete source directory filesystem identity changed before stability leasing.");
        }
    }

    private static void ValidateFileHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity)
    {
        var information = GetInformation(handle, "authorized delete source file");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The authorized delete source changed type before stability leasing.");
        }

        var actualPath = GetFinalPath(handle, "authorized delete source file");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The authorized delete source canonical path changed. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        if (ToIdentity(information) != expectedIdentity)
        {
            throw new IOException(
                "The authorized delete source filesystem identity changed before stability leasing.");
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
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                0);
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

        throw new IOException(
            "Resolving the final delete stability handle path exceeded the supported buffer growth attempts.");
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) &&
            path.Length >= 6 &&
            path[5] == Path.VolumeSeparatorChar)
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
        return new IOException(
            $"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}");
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

    private sealed class StabilityLease : IFileDeleteOperationStabilityLease
    {
        private SafeFileHandle? _sourceDirectory;
        private SafeFileHandle? _sourceFile;

        public StabilityLease(
            FileDeleteOperationStabilityLeaseEvidence evidence,
            SafeFileHandle sourceDirectory,
            SafeFileHandle sourceFile)
        {
            Evidence = evidence;
            _sourceDirectory = sourceDirectory;
            _sourceFile = sourceFile;
        }

        public FileDeleteOperationStabilityLeaseEvidence Evidence { get; }

        public bool DeleteMutationAuthorized => false;

        public ValueTask DisposeAsync()
        {
            DisposeNoThrow(_sourceFile);
            _sourceFile = null;
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
