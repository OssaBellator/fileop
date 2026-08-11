using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Storage;

public sealed class WindowsStorageCleanupReadinessService
{
    private readonly IFileOperationCanonicalPathResolver _canonicalResolver;
    private readonly WindowsCurrentReviewFileEvidenceReader _currentFileReader;

    public WindowsStorageCleanupReadinessService(
        IFileOperationCanonicalPathResolver? canonicalResolver = null,
        WindowsCurrentReviewFileEvidenceReader? currentFileReader = null)
    {
        _canonicalResolver = canonicalResolver ?? new WindowsFileOperationCanonicalPathResolver();
        _currentFileReader = currentFileReader ?? new WindowsCurrentReviewFileEvidenceReader();
    }

    public async ValueTask<StorageCleanupReadinessPreview> PreviewAsync(
        StorageReviewCandidate candidate,
        string reviewRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalRoot = await _canonicalResolver
            .ResolveAsync(reviewRoot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var canonicalCandidate = await _canonicalResolver
            .ResolveAsync(candidate.Path, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        StorageCleanupCurrentFileEvidence? currentFile = null;
        string? currentFileError = null;
        if (canonicalCandidate.State == FileOperationCanonicalPathState.File &&
            !canonicalCandidate.IsLeafReparsePoint)
        {
            try
            {
                currentFile = await _currentFileReader
                    .ReadAsync(candidate.Path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is Win32Exception or
                    IOException or
                    UnauthorizedAccessException or
                    SecurityException or
                    ArgumentException or
                    NotSupportedException)
            {
                currentFileError =
                    $"Current read-only file metadata could not be captured: {exception.Message}";
            }
        }

        return StorageCleanupReadinessAnalyzer.Analyze(
            candidate,
            reviewRoot,
            canonicalRoot,
            canonicalCandidate,
            currentFile,
            currentFileError,
            DateTimeOffset.UtcNow);
    }
}

public sealed class WindowsCurrentReviewFileEvidenceReader
{
    private const uint FileFlagBackupSemantics = 0x02000000;

    public ValueTask<StorageCleanupCurrentFileEvidence> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<StorageCleanupCurrentFileEvidence>(
            Task.Run(() => Read(path, cancellationToken), cancellationToken));
    }

    private static StorageCleanupCurrentFileEvidence Read(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedPath = Path.GetFullPath(path);
        var attributes = File.GetAttributes(requestedPath);
        var leafIsReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

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
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not open current cleanup-readiness metadata for {requestedPath}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not read current cleanup-readiness metadata for {requestedPath}.");
        }
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException(
                $"The cleanup-readiness candidate is currently a directory: {requestedPath}.");
        }

        var finalPath = GetFinalPath(handle);
        var logicalBytesUnsigned = CombineUnsigned(information.FileSizeHigh, information.FileSizeLow);
        if (logicalBytesUnsigned > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Windows reported a file size outside FileOp's signed-byte range for {requestedPath}.");
        }

        var lastWriteRaw = CombineUnsigned(
            information.LastWriteTime.HighDateTime,
            information.LastWriteTime.LowDateTime);
        if (lastWriteRaw > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Windows reported an invalid last-write FILETIME for {requestedPath}.");
        }

        return new StorageCleanupCurrentFileEvidence(
            requestedPath,
            NormalizeFinalPath(finalPath),
            new FileIdentity(
                information.VolumeSerialNumber,
                CombineUnsigned(information.FileIndexHigh, information.FileIndexLow)),
            (long)logicalBytesUnsigned,
            new DateTimeOffset(DateTime.FromFileTimeUtc((long)lastWriteRaw), TimeSpan.Zero),
            leafIsReparsePoint);
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not resolve the current cleanup-readiness final path.");
            }
            if (length < buffer.Capacity)
            {
                return buffer.ToString();
            }
            capacity = checked((int)length + 1);
        }

        throw new IOException("The current cleanup-readiness final path exceeded the supported buffer growth limit.");
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

    private static ulong CombineUnsigned(uint high, uint low) =>
        ((ulong)high << 32) | low;

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
