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
/// Produces primary-stream SHA-256 evidence through one Windows file handle.
/// The handle denies write/delete sharing for the lifetime of validation + hashing.
/// </summary>
public sealed class WindowsFileContentFingerprintReader : IFileContentFingerprintReader
{
    private const uint FileReadData = 0x0001;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;

    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int BufferSize = 1024 * 1024;

    public ValueTask<FileContentFingerprintReadResult> ReadAsync(
        string canonicalPath,
        FileIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<FileContentFingerprintReadResult>(
            Task.Run(
                () => Read(canonicalPath, expectedIdentity, cancellationToken),
                cancellationToken));
    }

    private static FileContentFingerprintReadResult Read(
        string canonicalPath,
        FileIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        string requestedPath;
        try
        {
            requestedPath = NormalizeForComparison(canonicalPath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error(canonicalPath, "InvalidPath", exception.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var handle = CreateFileW(
            requestedPath,
            FileReadData | FileReadAttributes | Synchronize,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return FromWin32Failure(requestedPath, Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(handle, out var before))
        {
            return FromWin32Failure(requestedPath, Marshal.GetLastWin32Error());
        }

        var finalPath = TryGetFinalPath(handle, out var finalPathError);
        if (finalPath is null)
        {
            return FromWin32Failure(requestedPath, finalPathError);
        }

        var canonicalFinalPath = NormalizeFinalPath(finalPath);
        var isDirectory = (before.FileAttributes & (uint)FileAttributes.Directory) != 0;
        var isReparsePoint = (before.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
        var identity = ToIdentity(before);
        var current = new FileOperationCanonicalPath(
            requestedPath,
            canonicalFinalPath,
            isDirectory
                ? FileOperationCanonicalPathState.Directory
                : FileOperationCanonicalPathState.File,
            isReparsePoint,
            identity);

        if (isReparsePoint)
        {
            return new FileContentFingerprintReadResult(
                FileContentFingerprintReadStatus.ReparsePoint,
                current,
                ContentFingerprint: null,
                "The stable read handle opened a reparse-point leaf.");
        }

        if (!PathsEqual(canonicalFinalPath, requestedPath))
        {
            return new FileContentFingerprintReadResult(
                FileContentFingerprintReadStatus.Redirected,
                current,
                ContentFingerprint: null,
                "The stable read handle resolved to a different canonical location.");
        }

        if (isDirectory)
        {
            return new FileContentFingerprintReadResult(
                FileContentFingerprintReadStatus.UnexpectedType,
                current,
                ContentFingerprint: null,
                "The recorded file destination now resolves to a directory.");
        }

        if (identity != expectedIdentity)
        {
            return new FileContentFingerprintReadResult(
                FileContentFingerprintReadStatus.DifferentObject,
                current,
                ContentFingerprint: null,
                "The stable read handle has a different FileIdentity than durable recovery history.");
        }

        FileContentFingerprint fingerprint;
        try
        {
            fingerprint = HashMainStream(handle, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException exception)
        {
            return Error(requestedPath, "ReadFailed", exception.Message, current);
        }

        if (!GetFileInformationByHandle(handle, out var after))
        {
            return FromWin32Failure(requestedPath, Marshal.GetLastWin32Error(), current);
        }

        if (ToIdentity(after) != expectedIdentity ||
            FileSize(before) != FileSize(after) ||
            ToUInt64(before.LastWriteTime) != ToUInt64(after.LastWriteTime))
        {
            return Error(
                requestedPath,
                "ChangedDuringRead",
                "The destination's identity, size, or last-write timestamp changed while its primary stream was being hashed.",
                current);
        }

        var finalPathAfterRead = TryGetFinalPath(handle, out finalPathError);
        if (finalPathAfterRead is null)
        {
            return FromWin32Failure(requestedPath, finalPathError, current);
        }

        if (!PathsEqual(NormalizeFinalPath(finalPathAfterRead), requestedPath))
        {
            return new FileContentFingerprintReadResult(
                FileContentFingerprintReadStatus.Redirected,
                current,
                ContentFingerprint: null,
                "The destination canonical location changed while its primary stream was being hashed.");
        }

        return new FileContentFingerprintReadResult(
            FileContentFingerprintReadStatus.Success,
            current,
            fingerprint,
            "The destination primary stream was hashed through a stable identity-bound read handle.");
    }

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

    private static FileContentFingerprintReadResult FromWin32Failure(
        string path,
        int error,
        FileOperationCanonicalPath? current = null)
    {
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return new FileContentFingerprintReadResult(
                FileContentFingerprintReadStatus.Missing,
                current ?? Missing(path),
                ContentFingerprint: null,
                $"The destination is missing (Win32 error {error}).");
        }

        if (error == ErrorSharingViolation)
        {
            return new FileContentFingerprintReadResult(
                FileContentFingerprintReadStatus.Busy,
                current ?? ErrorPath(path, "SharingViolation", error),
                ContentFingerprint: null,
                "Existing sharing constraints are incompatible with the stable read proof.");
        }

        if (error == ErrorAccessDenied)
        {
            return new FileContentFingerprintReadResult(
                FileContentFingerprintReadStatus.Inaccessible,
                current ?? Inaccessible(path, error),
                ContentFingerprint: null,
                "The destination could not be opened for stable read access.");
        }

        return new FileContentFingerprintReadResult(
            FileContentFingerprintReadStatus.Error,
            current ?? ErrorPath(path, $"Win32:{error}", error),
            ContentFingerprint: null,
            $"Stable destination content verification failed with Win32 error {error}.");
    }

    private static FileContentFingerprintReadResult Error(
        string path,
        string code,
        string message,
        FileOperationCanonicalPath? current = null) =>
        new(
            FileContentFingerprintReadStatus.Error,
            current ?? new FileOperationCanonicalPath(
                path,
                path,
                FileOperationCanonicalPathState.Error,
                IsLeafReparsePoint: false,
                ErrorCode: code,
                ErrorMessage: message),
            ContentFingerprint: null,
            message);

    private static FileOperationCanonicalPath Missing(string path) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Missing,
            IsLeafReparsePoint: false);

    private static FileOperationCanonicalPath Inaccessible(string path, int error) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Inaccessible,
            IsLeafReparsePoint: false,
            ErrorCode: "AccessDenied",
            ErrorMessage: $"Windows stable read open failed with error {error}.");

    private static FileOperationCanonicalPath ErrorPath(string path, string code, int error) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Error,
            IsLeafReparsePoint: false,
            ErrorCode: code,
            ErrorMessage: $"Windows stable read open failed with error {error}.");

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

    private static IOException Win32IOException(string action)
    {
        var error = Marshal.GetLastWin32Error();
        return new IOException($"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
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
