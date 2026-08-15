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
/// Collects non-authorizing source-only evidence before cross-volume Copy. The handle is
/// deliberately opened with read/write/delete sharing so this early UX/cost probe is not
/// misrepresented as a stability lease. Exact identity/path binding is still required, and
/// the later destructive fidelity verifier remains the authority-relevant checkpoint.
/// </summary>
public sealed class WindowsFileCrossVolumeMoveSourcePreflightProbe :
    IFileCrossVolumeMoveSourcePreflightProbe
{
    private const uint FileReadData = 0x00000001u;
    private const uint FileReadAttributes = 0x00000080u;
    private const uint Synchronize = 0x00100000u;
    private const uint FileFlagOpenReparsePoint = 0x00200000u;

    public ValueTask<FileCrossVolumeMoveSourcePreflightClassification> ProbeAsync(
        FileCrossVolumeMoveSourcePreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<FileCrossVolumeMoveSourcePreflightClassification>(
            Task.Run(() => Probe(request, cancellationToken), cancellationToken));
    }

    private static FileCrossVolumeMoveSourcePreflightClassification Probe(
        FileCrossVolumeMoveSourcePreflightRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var source = OpenSource(request);
        var before = GetVerifiedInformation(source, request);
        cancellationToken.ThrowIfCancellationRequested();
        var streams = WindowsFileNamedDataStreamTopologyDigest.Read(source);
        cancellationToken.ThrowIfCancellationRequested();
        var extendedAttributeSize = ReadExtendedAttributeSize(source);
        var after = GetVerifiedInformation(source, request);
        EnsureStableDuringRead(before, after);

        return FileCrossVolumeMoveSourcePreflightClassifier.Classify(
            new FileCrossVolumeMoveSourcePreflightEvidence(
                ToBasicMetadata(after),
                streams.NamedStreamCount,
                extendedAttributeSize));
    }

    private static SafeFileHandle OpenSource(FileCrossVolumeMoveSourcePreflightRequest request)
    {
        var handle = CreateFileW(
            request.CanonicalSourcePath,
            FileReadData | FileReadAttributes | Synchronize,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var exception = Win32IOException(
                $"Opening cross-volume Move source preflight '{request.CanonicalSourcePath}'");
            handle.Dispose();
            throw exception;
        }

        try
        {
            GetVerifiedInformation(handle, request);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static ByHandleFileInformation GetVerifiedInformation(
        SafeFileHandle handle,
        FileCrossVolumeMoveSourcePreflightRequest request)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw Win32IOException("Reading cross-volume Move source preflight identity");
        }
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "The cross-volume Move source preflight target is not an ordinary non-reparse file.");
        }

        var identity = new FileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        if (identity != request.SourceIdentity ||
            !PathsEqual(
                GetFinalPath(handle, "cross-volume Move source preflight"),
                request.CanonicalSourcePath))
        {
            throw new IOException(
                "The cross-volume Move source canonical path or filesystem identity changed during preflight.");
        }

        return information;
    }

    private static uint ReadExtendedAttributeSize(SafeFileHandle handle)
    {
        var status = NtQueryInformationFile(
            handle,
            out _,
            out var information,
            checked((uint)Marshal.SizeOf<FileEaInformation>()),
            FileInformationClass.FileEaInformation);
        if (status < 0)
        {
            throw new IOException(
                $"NtQueryInformationFile(FileEaInformation) for cross-volume Move source preflight failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
        }

        return information.EaSize;
    }

    private static void EnsureStableDuringRead(
        ByHandleFileInformation before,
        ByHandleFileInformation after)
    {
        if (before.VolumeSerialNumber != after.VolumeSerialNumber ||
            before.FileIndexHigh != after.FileIndexHigh ||
            before.FileIndexLow != after.FileIndexLow ||
            before.FileSizeHigh != after.FileSizeHigh ||
            before.FileSizeLow != after.FileSizeLow ||
            ToUInt64(before.LastWriteTime) != ToUInt64(after.LastWriteTime) ||
            before.FileAttributes != after.FileAttributes ||
            before.NumberOfLinks != after.NumberOfLinks)
        {
            throw new IOException(
                "The exact cross-volume Move source changed while read-only preflight evidence was being collected.");
        }
    }

    private static FileBasicMetadataEvidence ToBasicMetadata(ByHandleFileInformation information) =>
        new(
            ToUInt64(information.CreationTime),
            ToUInt64(information.LastAccessTime),
            ToUInt64(information.LastWriteTime),
            information.FileAttributes);

    private static ulong ToUInt64(FileTime value) =>
        ((ulong)value.HighDateTime << 32) | value.LowDateTime;

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

        throw new IOException($"Resolving final path for {description} exceeded buffer growth limits.");
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
        return new IOException(
            $"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}");
    }

    private enum FileInformationClass : int
    {
        FileEaInformation = 7,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileEaInformation
    {
        public uint EaSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
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
        "ntdll.dll",
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern int NtQueryInformationFile(
        SafeFileHandle FileHandle,
        out IoStatusBlock IoStatusBlock,
        out FileEaInformation FileInformation,
        uint Length,
        FileInformationClass FileInformationClass);
}
