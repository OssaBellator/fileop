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
/// Reads fail-closed cross-volume Move data/metadata fidelity evidence while the raw
/// source-delete lease keeps the exact source/destination namespace objects pinned.
/// This component never grants or performs source deletion.
///
/// Security-descriptor equivalence is intentionally not queried here. FileOp follows
/// Windows cross-volume Move semantics: the new destination receives destination-side
/// default/inherited security rather than preserving the source descriptor. Requiring
/// privileged SACL reads would contradict that deliberate product contract.
/// </summary>
internal sealed class WindowsFileCrossVolumeMoveFidelityVerifier :
    IFileCrossVolumeMoveFidelityVerifier
{
    private const uint FileReadData = 0x00000001u;
    private const uint FileReadAttributes = 0x00000080u;
    private const uint Synchronize = 0x00100000u;
    private const uint FileFlagOpenReparsePoint = 0x00200000u;
    private const uint FileFlagSequentialScan = 0x08000000u;
    private const int BufferSize = 1024 * 1024;

    public ValueTask<FileCrossVolumeMoveFidelityClassification> VerifyAsync(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<FileCrossVolumeMoveFidelityClassification>(
            Task.Run(() => Verify(request, cancellationToken), cancellationToken));
    }

    private static FileCrossVolumeMoveFidelityClassification Verify(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var source = OpenEvidenceFile(
            request.CanonicalSourcePath,
            request.SourceIdentity,
            "cross-volume Move source fidelity");
        using var destination = OpenEvidenceFile(
            request.CanonicalDestinationPath,
            request.DestinationIdentity,
            "cross-volume Move destination fidelity");

        var sourceBefore = GetVerifiedInformation(
            source,
            request.CanonicalSourcePath,
            request.SourceIdentity,
            "cross-volume Move source fidelity");
        var destinationBefore = GetVerifiedInformation(
            destination,
            request.CanonicalDestinationPath,
            request.DestinationIdentity,
            "cross-volume Move destination fidelity");

        var sourceFingerprint = HashMainStream(
            source,
            "cross-volume Move source fidelity",
            cancellationToken);
        var destinationFingerprint = HashMainStream(
            destination,
            "cross-volume Move destination fidelity",
            cancellationToken);

        var sourceStreams = WindowsFileNamedDataStreamTopologyDigest.Read(source);
        var destinationStreams = WindowsFileNamedDataStreamTopologyDigest.Read(destination);
        var sourceEaSize = ReadExtendedAttributeSize(source, "cross-volume Move source fidelity");
        var destinationEaSize = ReadExtendedAttributeSize(destination, "cross-volume Move destination fidelity");

        var sourceAfter = GetVerifiedInformation(
            source,
            request.CanonicalSourcePath,
            request.SourceIdentity,
            "cross-volume Move source fidelity");
        var destinationAfter = GetVerifiedInformation(
            destination,
            request.CanonicalDestinationPath,
            request.DestinationIdentity,
            "cross-volume Move destination fidelity");
        EnsureStableDuringRead(sourceBefore, sourceAfter, "source");
        EnsureStableDuringRead(destinationBefore, destinationAfter, "destination");

        var evidence = new FileCrossVolumeMoveFidelityEvidence(
            request.DestinationContentFingerprint,
            sourceFingerprint,
            destinationFingerprint,
            ToBasicMetadata(sourceAfter),
            ToBasicMetadata(destinationAfter),
            sourceStreams.NamedStreamCount,
            destinationStreams.NamedStreamCount,
            sourceAfter.NumberOfLinks,
            destinationAfter.NumberOfLinks,
            sourceEaSize,
            destinationEaSize);
        return FileCrossVolumeMoveFidelityClassifier.Classify(evidence);
    }

    private static SafeFileHandle OpenEvidenceFile(
        string canonicalPath,
        FileIdentity expectedIdentity,
        string description)
    {
        // The already-live raw source-delete lease holds DELETE access to the source.
        // A new evidence handle must therefore share DELETE as well as READ or Windows
        // rejects the reopen with ERROR_SHARING_VIOLATION. The raw lease itself still
        // uses FileShare.Read, so this compatibility flag does not allow a third party
        // to obtain delete access while that lease remains alive.
        var handle = CreateFileW(
            canonicalPath,
            FileReadData | FileReadAttributes | Synchronize,
            FileShare.Read | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var exception = Win32IOException($"Opening {description} '{canonicalPath}'");
            handle.Dispose();
            throw exception;
        }

        try
        {
            GetVerifiedInformation(handle, canonicalPath, expectedIdentity, description);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static uint ReadExtendedAttributeSize(
        SafeFileHandle handle,
        string description)
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
                $"NtQueryInformationFile(FileEaInformation) for {description} failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
        }

        return information.EaSize;
    }

    private static FileContentFingerprint HashMainStream(
        SafeFileHandle handle,
        string description,
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
                    throw Win32IOException("Reading " + description);
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

    private static ByHandleFileInformation GetVerifiedInformation(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity,
        string description)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw Win32IOException("Reading identity for " + description);
        }
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The {description} is not an ordinary non-reparse file.");
        }

        var identity = new FileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        if (identity != expectedIdentity ||
            !PathsEqual(GetFinalPath(handle, description), expectedCanonicalPath))
        {
            throw new IOException(
                $"The {description} canonical path or filesystem identity changed during fidelity verification.");
        }
        return information;
    }

    private static void EnsureStableDuringRead(
        ByHandleFileInformation before,
        ByHandleFileInformation after,
        string description)
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
                $"The pinned cross-volume Move {description} changed while fidelity evidence was being read.");
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
        "kernel32.dll",
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

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
