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
/// Reacquires the exact authorized root/direct-child file under a final DELETE-capable
/// handle lease. Acquisition proves only that Windows granted the requested capability;
/// this provider exposes no mutation method and never changes filesystem state.
/// </summary>
public sealed class WindowsFileDeleteOperationFinalMutationLeaseProvider :
    IFileDeleteOperationFinalMutationLeaseProvider
{
    private const uint Delete = 0x00010000;
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

    public WindowsFileDeleteOperationFinalMutationLeaseProvider(
        IFileDeleteProtectedLocationPolicy? protectedLocationPolicy = null)
    {
        _protectedLocationPolicy = protectedLocationPolicy ??
            new WindowsFileDeleteProtectedLocationPolicy();
    }

    public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
        FileDeleteOperationFinalMutationLeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);

        return new ValueTask<IFileDeleteOperationFinalMutationLease>(
            Task.Run(() => Acquire(request), cancellationToken));
    }

    private IFileDeleteOperationFinalMutationLease Acquire(
        FileDeleteOperationFinalMutationLeaseRequest request)
    {
        var authorization = request.Authorization;
        var expectedRootPath = NormalizeForComparison(
            authorization.CanonicalSourceDirectoryPath);
        var expectedSourcePath = NormalizeForComparison(
            request.AuthorizedItem.CanonicalPath);
        var expectedRootIdentity = authorization.SourceDirectoryIdentity;
        var expectedSourceIdentity = request.AuthorizedItem.Identity;

        var sourceParent = Path.GetDirectoryName(expectedSourcePath)
            ?? throw new InvalidOperationException(
                "The authorized final-delete file has no canonical parent directory.");
        if (!PathsEqual(sourceParent, expectedRootPath))
        {
            throw new InvalidOperationException(
                "The authorized final-delete file is no longer a direct child of the authorized canonical root.");
        }

        var leafName = Path.GetFileName(expectedSourcePath);
        if (string.IsNullOrWhiteSpace(leafName) ||
            !string.Equals(leafName, request.Entry.Name, StringComparison.OrdinalIgnoreCase) ||
            leafName.Contains(Path.DirectorySeparatorChar) ||
            leafName.Contains(Path.AltDirectorySeparatorChar) ||
            leafName.Contains(Path.VolumeSeparatorChar))
        {
            throw new InvalidOperationException(
                "The authorized final-delete leaf name is not one safe exact direct-child component.");
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
                Delete | FileReadAttributes | Synchronize,
                FileShare.Read,
                FileOpen,
                FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint);
            ValidateFileHandle(
                sourceFile,
                expectedSourcePath,
                expectedSourceIdentity);

            // The root remains held and is checked again after the relative leaf open so the
            // accepted final evidence describes one exact live root/direct-child handle set.
            ValidateDirectoryHandle(
                sourceDirectory,
                expectedRootPath,
                expectedRootIdentity);

            var evidence = new FileDeleteOperationFinalMutationLeaseEvidence(
                request,
                authorization.CanonicalSourceDirectoryPath,
                expectedRootIdentity,
                request.AuthorizedItem.CanonicalPath,
                expectedSourceIdentity);
            var lease = new FinalMutationLease(
                evidence,
                sourceDirectory,
                sourceFile);
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

    private static void ValidateRequest(FileDeleteOperationFinalMutationLeaseRequest request)
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
                "Windows final delete leasing requires one exact authorized file request that remains non-authorizing for mutation.",
                nameof(request));
        }
    }

    private void EnsureAllowedByProtectedLocationPolicy(
        string canonicalPath,
        string description)
    {
        var decision = _protectedLocationPolicy.Evaluate(canonicalPath);
        if (decision.IsBlocked)
        {
            throw new UnauthorizedAccessException(
                $"The canonical final-delete {description} is protected: {decision.Reason}");
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
                $"Opening authorized canonical final-delete directory '{canonicalPath}'");
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
                "A relative final-delete leaf must contain only one path component.",
                nameof(leafName));
        }

        var nameBuffer = Marshal.StringToHGlobalUni(leafName);
        var unicodeString = new UnicodeString
        {
            Length = checked((ushort)(leafName.Length * sizeof(char))),
            MaximumLength = checked((ushort)((leafName.Length + 1) * sizeof(char))),
            Buffer = nameBuffer,
        };
        var unicodeStringPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<UnicodeString>());
        var rootAddedRef = false;
        try
        {
            Marshal.StructureToPtr(
                unicodeString,
                unicodeStringPointer,
                fDeleteOld: false);
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
                    $"NtCreateFile for authorized relative final-delete leaf '{leafName}' failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }

            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            {
                throw new IOException(
                    "NtCreateFile returned success without a valid final delete-capability handle.");
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
        var information = GetInformation(
            handle,
            "authorized final-delete source directory");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The authorized final-delete source directory changed into an unsafe object before final leasing.");
        }

        var actualPath = GetFinalPath(
            handle,
            "authorized final-delete source directory");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The authorized final-delete source directory canonical path changed. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        if (ToIdentity(information) != expectedIdentity)
        {
            throw new IOException(
                "The authorized final-delete source directory filesystem identity changed before final leasing.");
        }
    }

    private static void ValidateFileHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity)
    {
        var information = GetInformation(handle, "authorized final-delete source file");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The authorized final-delete source changed type before final leasing.");
        }

        var actualPath = GetFinalPath(handle, "authorized final-delete source file");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The authorized final-delete source canonical path changed. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        if (ToIdentity(information) != expectedIdentity)
        {
            throw new IOException(
                "The authorized final-delete source filesystem identity changed before final leasing.");
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
            "Resolving the final-delete handle path exceeded the supported buffer growth attempts.");
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

    private static bool IsLive(SafeFileHandle? handle) =>
        handle is not null && !handle.IsInvalid && !handle.IsClosed;

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

    private sealed class FinalMutationLease : IFileDeleteOperationFinalMutationLease
    {
        private SafeFileHandle? _sourceDirectory;
        private SafeFileHandle? _sourceFile;

        public FinalMutationLease(
            FileDeleteOperationFinalMutationLeaseEvidence evidence,
            SafeFileHandle sourceDirectory,
            SafeFileHandle sourceFile)
        {
            Evidence = evidence;
            _sourceDirectory = sourceDirectory;
            _sourceFile = sourceFile;
        }

        public FileDeleteOperationFinalMutationLeaseEvidence Evidence { get; }

        public bool DeleteAccessCapabilityHeld =>
            IsLive(_sourceDirectory) && IsLive(_sourceFile);

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
