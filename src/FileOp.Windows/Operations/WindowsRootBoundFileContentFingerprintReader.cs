using System;
using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

/// <summary>
/// Verifies a recovery destination by opening the recorded destination root,
/// validating that directory handle, opening the leaf relative to that root with
/// NtCreateFile, and holding both handles through SHA-256 and post-read checks.
/// A successful result also reports the leaf handle's stable hard-link count.
/// </summary>
public sealed class WindowsRootBoundFileContentFingerprintReader : IRootBoundFileContentFingerprintReader
{
    private const uint FileReadData = 0x0001;
    private const uint FileTraverse = 0x0020;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;

    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileSequentialOnly = 0x00000004;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint ObjCaseInsensitive = 0x00000040;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    private const int StatusSharingViolation = unchecked((int)0xC0000043);
    private const int StatusFileIsADirectory = unchecked((int)0xC00000BA);

    private const int BufferSize = 1024 * 1024;

    public ValueTask<FileContentFingerprintReadResult> ReadAsync(
        FileContentFingerprintReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CanonicalDestinationDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CanonicalDestinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<FileContentFingerprintReadResult>(
            Task.Run(() => Read(request, cancellationToken), cancellationToken));
    }

    private static FileContentFingerprintReadResult Read(
        FileContentFingerprintReadRequest request,
        CancellationToken cancellationToken)
    {
        string rootPath;
        string leafPath;
        try
        {
            rootPath = NormalizeForComparison(request.CanonicalDestinationDirectoryPath);
            leafPath = NormalizeForComparison(request.CanonicalDestinationPath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error(
                request.CanonicalDestinationDirectoryPath,
                request.CanonicalDestinationPath,
                "InvalidPath",
                exception.Message);
        }

        var parentPath = Path.GetDirectoryName(leafPath);
        var leafName = Path.GetFileName(leafPath);
        if (string.IsNullOrWhiteSpace(parentPath) ||
            string.IsNullOrWhiteSpace(leafName) ||
            !PathsEqual(parentPath, rootPath) ||
            leafName.Contains(Path.DirectorySeparatorChar) ||
            leafName.Contains(Path.AltDirectorySeparatorChar) ||
            leafName.Contains(Path.VolumeSeparatorChar))
        {
            return Error(
                rootPath,
                leafPath,
                "InvalidRelativeLeaf",
                "The recorded destination leaf is not one path component directly beneath the recorded destination root.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var rootHandle = CreateFileW(
            rootPath,
            FileTraverse | FileReadAttributes | Synchronize,
            FileShare.ReadWrite,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (rootHandle.IsInvalid)
        {
            return RootOpenFailure(rootPath, leafPath, Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(rootHandle, out var rootBefore))
        {
            return RootOpenFailure(rootPath, leafPath, Marshal.GetLastWin32Error());
        }

        var rootFinalPath = TryGetFinalPath(rootHandle, out var finalPathError);
        if (rootFinalPath is null)
        {
            return RootOpenFailure(rootPath, leafPath, finalPathError);
        }

        var currentRoot = CreateCurrentPath(rootPath, rootFinalPath, rootBefore);
        if ((rootBefore.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (rootBefore.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            !PathsEqual(currentRoot.CanonicalPath, rootPath) ||
            currentRoot.Identity != request.DestinationDirectoryIdentity)
        {
            return RootChanged(
                currentRoot,
                leafPath,
                "The destination root no longer matches the recorded ordinary-directory path and FileIdentity.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var leafOpen = OpenRelativeLeaf(rootHandle, leafName);
        if (leafOpen.Handle is null)
        {
            return LeafOpenFailure(currentRoot, leafPath, leafOpen.Status);
        }

        using var leafHandle = leafOpen.Handle;
        if (!GetFileInformationByHandle(leafHandle, out var leafBefore))
        {
            return LeafWin32Failure(currentRoot, leafPath, Marshal.GetLastWin32Error());
        }

        var leafFinalPath = TryGetFinalPath(leafHandle, out finalPathError);
        if (leafFinalPath is null)
        {
            return LeafWin32Failure(currentRoot, leafPath, finalPathError);
        }

        var currentLeaf = CreateCurrentPath(leafPath, leafFinalPath, leafBefore);
        if ((leafBefore.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            return Result(
                FileContentFingerprintReadStatus.ReparsePoint,
                currentRoot,
                currentLeaf,
                "The root-relative stable read handle opened a reparse-point leaf.");
        }

        if (!PathsEqual(currentLeaf.CanonicalPath, leafPath))
        {
            return Result(
                FileContentFingerprintReadStatus.Redirected,
                currentRoot,
                currentLeaf,
                "The root-relative stable read handle resolved to a different canonical leaf location.");
        }

        if ((leafBefore.FileAttributes & (uint)FileAttributes.Directory) != 0)
        {
            return Result(
                FileContentFingerprintReadStatus.UnexpectedType,
                currentRoot,
                currentLeaf,
                "The recorded file destination now resolves to a directory.");
        }

        if (currentLeaf.Identity != request.DestinationIdentity)
        {
            return Result(
                FileContentFingerprintReadStatus.DifferentObject,
                currentRoot,
                currentLeaf,
                "The root-relative leaf handle has a different FileIdentity than durable recovery history.");
        }

        if (leafBefore.NumberOfLinks == 0)
        {
            return Error(
                rootPath,
                leafPath,
                "InvalidHardLinkCount",
                "The destination leaf reported a zero hard-link count before hashing.",
                currentRoot,
                currentLeaf);
        }

        FileContentFingerprint fingerprint;
        try
        {
            fingerprint = HashMainStream(leafHandle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException exception)
        {
            return Error(
                rootPath,
                leafPath,
                "ReadFailed",
                exception.Message,
                currentRoot,
                currentLeaf);
        }

        if (!GetFileInformationByHandle(rootHandle, out var rootAfter))
        {
            return Error(
                rootPath,
                leafPath,
                "RootPostReadInfoFailed",
                Win32Message("Reading destination-root identity after hashing"),
                currentRoot,
                currentLeaf);
        }

        var rootFinalPathAfterRead = TryGetFinalPath(rootHandle, out finalPathError);
        if (rootFinalPathAfterRead is null)
        {
            return Error(
                rootPath,
                leafPath,
                "RootPostReadPathFailed",
                new Win32Exception(finalPathError).Message,
                currentRoot,
                currentLeaf);
        }

        var currentRootAfter = CreateCurrentPath(rootPath, rootFinalPathAfterRead, rootAfter);
        if ((rootAfter.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (rootAfter.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            currentRootAfter.Identity != request.DestinationDirectoryIdentity ||
            !PathsEqual(currentRootAfter.CanonicalPath, rootPath))
        {
            return RootChanged(
                currentRootAfter,
                leafPath,
                "The destination root changed while its child primary stream was being hashed.",
                currentLeaf);
        }

        if (!GetFileInformationByHandle(leafHandle, out var leafAfter))
        {
            return Error(
                rootPath,
                leafPath,
                "LeafPostReadInfoFailed",
                Win32Message("Reading destination-leaf identity after hashing"),
                currentRootAfter,
                currentLeaf);
        }

        var leafFinalPathAfterRead = TryGetFinalPath(leafHandle, out finalPathError);
        if (leafFinalPathAfterRead is null)
        {
            return Error(
                rootPath,
                leafPath,
                "LeafPostReadPathFailed",
                new Win32Exception(finalPathError).Message,
                currentRootAfter,
                currentLeaf);
        }

        var currentLeafAfter = CreateCurrentPath(leafPath, leafFinalPathAfterRead, leafAfter);
        if (currentLeafAfter.Identity != request.DestinationIdentity ||
            FileSize(leafBefore) != FileSize(leafAfter) ||
            ToUInt64(leafBefore.LastWriteTime) != ToUInt64(leafAfter.LastWriteTime) ||
            leafBefore.NumberOfLinks != leafAfter.NumberOfLinks ||
            leafAfter.NumberOfLinks == 0)
        {
            return Error(
                rootPath,
                leafPath,
                "ChangedDuringRead",
                "The destination leaf identity, size, last-write timestamp, or hard-link count changed while its primary stream was being hashed.",
                currentRootAfter,
                currentLeafAfter);
        }

        if (!PathsEqual(currentLeafAfter.CanonicalPath, leafPath))
        {
            return Result(
                FileContentFingerprintReadStatus.Redirected,
                currentRootAfter,
                currentLeafAfter,
                "The root-relative destination canonical location changed while its primary stream was being hashed.");
        }

        return new FileContentFingerprintReadResult(
            FileContentFingerprintReadStatus.Success,
            currentLeafAfter,
            fingerprint,
            "The destination primary stream and hard-link count were observed while verified root and leaf handles remained bound and alive.",
            currentRootAfter)
        {
            CurrentDestinationHardLinkCount = leafAfter.NumberOfLinks,
        };
    }

    private static RelativeOpenResult OpenRelativeLeaf(
        SafeFileHandle rootDirectory,
        string leafName)
    {
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
                FileReadData | FileReadAttributes | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                (uint)FileShare.Read,
                FileOpen,
                FileSequentialOnly |
                    FileSynchronousIoNonAlert |
                    FileNonDirectoryFile |
                    FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                return new RelativeOpenResult(null, status);
            }

            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            {
                return new RelativeOpenResult(null, unchecked((int)0xC0000008));
            }

            return new RelativeOpenResult(new SafeFileHandle(rawHandle, ownsHandle: true), status);
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

    private static FileContentFingerprintReadResult LeafOpenFailure(
        FileOperationCanonicalPath currentRoot,
        string leafPath,
        int status) => status switch
    {
        StatusObjectNameNotFound or StatusObjectPathNotFound => Result(
            FileContentFingerprintReadStatus.Missing,
            currentRoot,
            Missing(leafPath),
            $"The destination leaf is missing (NTSTATUS 0x{unchecked((uint)status):X8})."),
        StatusSharingViolation => Result(
            FileContentFingerprintReadStatus.Busy,
            currentRoot,
            ErrorPath(leafPath, "SharingViolation", status),
            "Existing leaf sharing constraints are incompatible with the stable root-bound read proof."),
        StatusAccessDenied => Result(
            FileContentFingerprintReadStatus.Inaccessible,
            currentRoot,
            Inaccessible(leafPath, status),
            "The destination leaf could not be opened for stable root-bound read access."),
        StatusFileIsADirectory => Result(
            FileContentFingerprintReadStatus.UnexpectedType,
            currentRoot,
            ErrorPath(leafPath, "UnexpectedDirectory", status),
            "The recorded file destination now resolves to a directory."),
        _ => Result(
            FileContentFingerprintReadStatus.Error,
            currentRoot,
            ErrorPath(leafPath, $"NTSTATUS:{unchecked((uint)status):X8}", status),
            $"Root-relative destination open failed with NTSTATUS 0x{unchecked((uint)status):X8}."),
    };

    private static FileContentFingerprintReadResult RootOpenFailure(
        string rootPath,
        string leafPath,
        int error) => error switch
    {
        ErrorSharingViolation => Result(
            FileContentFingerprintReadStatus.Busy,
            ErrorPath(rootPath, "SharingViolation", error),
            ErrorPath(leafPath, "RootSharingViolation", error),
            "Existing sharing constraints are incompatible with holding the destination-root binding."),
        ErrorAccessDenied => Result(
            FileContentFingerprintReadStatus.Inaccessible,
            Inaccessible(rootPath, error),
            ErrorPath(leafPath, "RootAccessDenied", error),
            "The destination root could not be opened for stable namespace verification."),
        ErrorFileNotFound or ErrorPathNotFound => RootChanged(
            Missing(rootPath),
            leafPath,
            $"The recorded destination root is missing (Win32 error {error})."),
        _ => Error(
            rootPath,
            leafPath,
            $"Win32:{error}",
            $"Stable destination-root verification failed with Win32 error {error}."),
    };

    private static FileContentFingerprintReadResult LeafWin32Failure(
        FileOperationCanonicalPath currentRoot,
        string leafPath,
        int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound => Result(
            FileContentFingerprintReadStatus.Missing,
            currentRoot,
            Missing(leafPath),
            $"The destination leaf is missing (Win32 error {error})."),
        ErrorSharingViolation => Result(
            FileContentFingerprintReadStatus.Busy,
            currentRoot,
            ErrorPath(leafPath, "SharingViolation", error),
            "Existing leaf sharing constraints are incompatible with the stable root-bound read proof."),
        ErrorAccessDenied => Result(
            FileContentFingerprintReadStatus.Inaccessible,
            currentRoot,
            Inaccessible(leafPath, error),
            "The destination leaf could not be inspected for stable read access."),
        _ => Result(
            FileContentFingerprintReadStatus.Error,
            currentRoot,
            ErrorPath(leafPath, $"Win32:{error}", error),
            $"Stable destination-leaf verification failed with Win32 error {error}."),
    };

    private static FileContentFingerprintReadResult RootChanged(
        FileOperationCanonicalPath currentRoot,
        string leafPath,
        string message,
        FileOperationCanonicalPath? currentLeaf = null) =>
        Result(
            FileContentFingerprintReadStatus.DestinationRootChanged,
            currentRoot,
            currentLeaf ?? ErrorPath(leafPath, "DestinationRootChanged", 0),
            message);

    private static FileContentFingerprintReadResult Result(
        FileContentFingerprintReadStatus status,
        FileOperationCanonicalPath currentRoot,
        FileOperationCanonicalPath currentLeaf,
        string message) =>
        new(status, currentLeaf, ContentFingerprint: null, message, currentRoot);

    private static FileContentFingerprintReadResult Error(
        string rootPath,
        string leafPath,
        string code,
        string message,
        FileOperationCanonicalPath? currentRoot = null,
        FileOperationCanonicalPath? currentLeaf = null) =>
        new(
            FileContentFingerprintReadStatus.Error,
            currentLeaf ?? ErrorPath(leafPath, code, 0),
            ContentFingerprint: null,
            message,
            currentRoot ?? ErrorPath(rootPath, code, 0));

    private static FileContentFingerprint HashMainStream(
        SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var pointer = pinned.AddrOfPinnedObject();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReadFile(
                        handle,
                        pointer,
                        checked((uint)Math.Min(buffer.Length, BufferSize)),
                        out var bytesRead,
                        IntPtr.Zero))
                {
                    throw Win32IOException("Reading destination content for recovery verification");
                }

                if (bytesRead == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, checked((int)bytesRead));
            }
        }
        finally
        {
            pinned.Free();
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new FileContentFingerprint(
            FileContentFingerprintAlgorithm.Sha256,
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static FileOperationCanonicalPath CreateCurrentPath(
        string requestedPath,
        string finalPath,
        ByHandleFileInformation information)
    {
        var isDirectory = (information.FileAttributes & (uint)FileAttributes.Directory) != 0;
        return new FileOperationCanonicalPath(
            requestedPath,
            NormalizeFinalPath(finalPath),
            isDirectory
                ? FileOperationCanonicalPathState.Directory
                : FileOperationCanonicalPathState.File,
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0,
            ToIdentity(information));
    }

    private static FileOperationCanonicalPath Missing(string path) =>
        new(path, path, FileOperationCanonicalPathState.Missing, IsLeafReparsePoint: false);

    private static FileOperationCanonicalPath Inaccessible(string path, int error) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Inaccessible,
            IsLeafReparsePoint: false,
            ErrorCode: "AccessDenied",
            ErrorMessage: $"Stable read open failed with error/status {error}.");

    private static FileOperationCanonicalPath ErrorPath(string path, string code, int error) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Error,
            IsLeafReparsePoint: false,
            ErrorCode: code,
            ErrorMessage: error == 0 ? code : $"Stable read failed with error/status {error}.");

    private static FileIdentity ToIdentity(ByHandleFileInformation information) =>
        new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

    private static ulong FileSize(ByHandleFileInformation information) =>
        ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;

    private static ulong ToUInt64(FileTime value) =>
        ((ulong)value.HighDateTime << 32) | value.LowDateTime;

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

        error = 122;
        return null;
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

    private static string Win32Message(string action)
    {
        var error = Marshal.GetLastWin32Error();
        return $"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}";
    }

    private static IOException Win32IOException(string action)
    {
        var error = Marshal.GetLastWin32Error();
        return new IOException($"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
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

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    private sealed record RelativeOpenResult(SafeFileHandle? Handle, int Status);

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
