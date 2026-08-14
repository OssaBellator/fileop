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
/// Adds a destructive-fidelity gate around the reviewed identity-bound source-delete
/// primitive. The inner lease is acquired first, so its exact source/destination handles
/// already deny ordinary write/delete sharing while this wrapper reads current evidence.
/// The same evidence is checked again after Core's durable source-delete barrier and before
/// the inner primitive receives mutation authority. Evidence never grants delete authority.
/// </summary>
public sealed class WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive :
    IFileCrossVolumeMoveSourceDeletePrimitive
{
    private readonly IFileCrossVolumeMoveSourceDeletePrimitive _inner;

    public WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive()
        : this(new WindowsFileCrossVolumeMoveSourceDeletePrimitive())
    {
    }

    internal WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive(
        IFileCrossVolumeMoveSourceDeletePrimitive inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask<IFileCrossVolumeMoveSourceDeleteLease> AcquireAsync(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var innerLease = await _inner.AcquireAsync(request, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var classification = await Task.Run(
                    () => WindowsFileCrossVolumeMoveFidelityVerifier.Verify(
                        request,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfBlocked(classification, "before the source-delete barrier");

            var lease = new FidelityLease(innerLease, request);
            innerLease = null!;
            return lease;
        }
        finally
        {
            if (innerLease is not null)
            {
                await innerLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void ThrowIfBlocked(
        FileCrossVolumeMoveFidelityClassification classification,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(classification);
        if (classification.CanDeleteSourceAfterDurableBarrier)
        {
            return;
        }

        throw new NotSupportedException(
            $"Cross-volume Move retained the source because destructive fidelity proof failed {phase}. " +
            $"Blocking evidence: {string.Join(", ", classification.Blockers)}. " +
            "The committed destination remains a Copy result and grants no source-delete authority.");
    }

    private sealed class FidelityLease : IFileCrossVolumeMoveSourceDeleteLease
    {
        private IFileCrossVolumeMoveSourceDeleteLease? _inner;
        private readonly FileCrossVolumeMoveSourceDeleteRequest _request;
        private int _mutationAttempted;

        internal FidelityLease(
            IFileCrossVolumeMoveSourceDeleteLease inner,
            FileCrossVolumeMoveSourceDeleteRequest request)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _request = request ?? throw new ArgumentNullException(nameof(request));
            if (!ReferenceEquals(inner.Evidence.Request, request))
            {
                throw new InvalidOperationException(
                    "The fidelity wrapper requires the exact source-delete request bound to the inner live lease.");
            }
        }

        public FileCrossVolumeMoveSourceDeleteEvidence Evidence =>
            Volatile.Read(ref _inner)?.Evidence ??
            throw new ObjectDisposedException(nameof(FidelityLease));

        public bool DeleteAccessCapabilityHeld =>
            Volatile.Read(ref _inner)?.DeleteAccessCapabilityHeld == true;

        public bool SourceDeleteMutationPerformed =>
            Volatile.Read(ref _inner)?.SourceDeleteMutationPerformed == true;

        public async ValueTask MarkDeletePendingAsync(
            FileCrossVolumeMoveSourceDeleteAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            cancellationToken.ThrowIfCancellationRequested();
            var inner = Volatile.Read(ref _inner)
                ?? throw new ObjectDisposedException(nameof(FidelityLease));
            if (!authorization.SourceDeleteBarrierSatisfied ||
                !authorization.SourceDeleteMutationAuthorized ||
                !authorization.IsBoundTo(inner.Evidence))
            {
                throw new UnauthorizedAccessException(
                    "Cross-volume Move fidelity verification cannot substitute for the exact Core-minted post-barrier source-delete authority.");
            }

            if (Interlocked.CompareExchange(ref _mutationAttempted, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Cross-volume Move source deletion is single-use even when fidelity revalidation refuses mutation.");
            }

            // This is already inside the durable source-delete critical section. Do not turn
            // a late cancellation into a partially-authorized destructive retry. Revalidate
            // synchronously to a terminal proof/refusal, then let the inner reviewed primitive
            // perform the exact same-handle disposition without a cancellable gap.
            var classification = await Task.Run(
                    () => WindowsFileCrossVolumeMoveFidelityVerifier.Verify(
                        _request,
                        CancellationToken.None),
                    CancellationToken.None)
                .ConfigureAwait(false);
            ThrowIfBlocked(classification, "after the durable source-delete barrier");

            await inner.MarkDeletePendingAsync(authorization, CancellationToken.None)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            var inner = Interlocked.Exchange(ref _inner, null);
            if (inner is not null)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

internal static class WindowsFileCrossVolumeMoveFidelityVerifier
{
    private const uint FileReadData = 0x00000001u;
    private const uint ReadControl = 0x00020000u;
    private const uint AccessSystemSecurity = 0x01000000u;
    private const uint FileReadAttributes = 0x00000080u;
    private const uint Synchronize = 0x00100000u;
    private const uint FileFlagOpenReparsePoint = 0x00200000u;
    private const uint FileFlagSequentialScan = 0x08000000u;
    private const uint BackupSecurityInformation = 0x00010000u;
    private const int ErrorInsufficientBuffer = 122;
    private const uint MaximumSecurityDescriptorBytes = 1024u * 1024u;
    private const int BufferSize = 1024 * 1024;

    internal static FileCrossVolumeMoveFidelityClassification Verify(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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

        var sourceSecurity = TryReadCompleteSecurityDigest(
            request.CanonicalSourcePath,
            request.SourceIdentity,
            "cross-volume Move source security");
        var destinationSecurity = TryReadCompleteSecurityDigest(
            request.CanonicalDestinationPath,
            request.DestinationIdentity,
            "cross-volume Move destination security");
        var securityComplete = sourceSecurity is not null && destinationSecurity is not null;
        var securityEquivalent = securityComplete && string.Equals(
            sourceSecurity,
            destinationSecurity,
            StringComparison.Ordinal);

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
            destinationEaSize,
            securityComplete,
            securityEquivalent);
        return FileCrossVolumeMoveFidelityClassifier.Classify(evidence);
    }

    private static SafeFileHandle OpenEvidenceFile(
        string canonicalPath,
        FileIdentity expectedIdentity,
        string description)
    {
        var handle = CreateFileW(
            canonicalPath,
            FileReadData | FileReadAttributes | ReadControl | Synchronize,
            FileShare.Read,
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

    private static string? TryReadCompleteSecurityDigest(
        string canonicalPath,
        FileIdentity expectedIdentity,
        string description)
    {
        using var handle = CreateFileW(
            canonicalPath,
            FileReadAttributes | ReadControl | AccessSystemSecurity | Synchronize,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        try
        {
            GetVerifiedInformation(handle, canonicalPath, expectedIdentity, description);
            return ReadCompleteSecurityDigest(handle);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadCompleteSecurityDigest(SafeFileHandle handle)
    {
        var succeeded = GetKernelObjectSecurity(
            handle,
            BackupSecurityInformation,
            IntPtr.Zero,
            0,
            out var bytesNeeded);
        if (succeeded || bytesNeeded == 0)
        {
            return null;
        }

        var sizeError = Marshal.GetLastWin32Error();
        if (sizeError != ErrorInsufficientBuffer ||
            bytesNeeded > MaximumSecurityDescriptorBytes ||
            bytesNeeded > int.MaxValue)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!GetKernelObjectSecurity(
                    handle,
                    BackupSecurityInformation,
                    buffer,
                    bytesNeeded,
                    out var actualBytes) ||
                actualBytes == 0 ||
                actualBytes > bytesNeeded ||
                actualBytes > int.MaxValue)
            {
                return null;
            }

            var descriptor = new byte[checked((int)actualBytes)];
            Marshal.Copy(buffer, descriptor, 0, descriptor.Length);
            return Convert.ToHexString(SHA256.HashData(descriptor)).ToLowerInvariant();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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
        "advapi32.dll",
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeFileHandle Handle,
        uint RequestedInformation,
        IntPtr pSecurityDescriptor,
        uint nLength,
        out uint lpnLengthNeeded);

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
