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
/// Acquires the destructive half of a cross-volume Move before the durable source-delete
/// barrier. The lease holds source root/file and destination root/file handles together.
/// Destination handles deny write/delete sharing and are content-verified before the lease
/// is returned, so the committed replacement cannot be swapped out between verification and
/// source deletion. Mutation remains impossible until Core supplies the exact post-barrier
/// authorization for this lease.
/// </summary>
public sealed class WindowsFileCrossVolumeMoveSourceDeletePrimitive :
    IFileCrossVolumeMoveSourceDeletePrimitive
{
    private const uint Delete = 0x00010000;
    private const uint FileReadData = 0x0001;
    private const uint FileTraverse = 0x0020;
    private const uint FileReadAttributes = 0x0080;
    private const uint Synchronize = 0x00100000;

    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileSupportsPosixUnlinkRename = 0x00000400;

    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileOpen = 1;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const int BufferSize = 1024 * 1024;

    private readonly IFileDeleteProtectedLocationPolicy _protectedLocationPolicy;
    private readonly IFileOperationNamespaceCapabilityProbe _namespaceCapabilityProbe;

    public WindowsFileCrossVolumeMoveSourceDeletePrimitive(
        IFileDeleteProtectedLocationPolicy? protectedLocationPolicy = null,
        IFileOperationNamespaceCapabilityProbe? namespaceCapabilityProbe = null)
    {
        _protectedLocationPolicy = protectedLocationPolicy ??
            new WindowsFileDeleteProtectedLocationPolicy();
        _namespaceCapabilityProbe = namespaceCapabilityProbe ??
            new WindowsFileOperationNamespaceCapabilityProbe();
    }

    public ValueTask<IFileCrossVolumeMoveSourceDeleteLease> AcquireAsync(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        return new ValueTask<IFileCrossVolumeMoveSourceDeleteLease>(
            Task.Run(() => Acquire(request, cancellationToken), cancellationToken));
    }

    internal static bool SupportsPosixUnlinkRename(uint fileSystemFlags) =>
        (fileSystemFlags & FileSupportsPosixUnlinkRename) != 0;

    private IFileCrossVolumeMoveSourceDeleteLease Acquire(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceRootPath = NormalizeForComparison(request.CanonicalSourceDirectoryPath);
        var destinationRootPath = NormalizeForComparison(request.CanonicalDestinationDirectoryPath);
        var sourcePath = NormalizeForComparison(request.CanonicalSourcePath);
        var destinationPath = NormalizeForComparison(request.CanonicalDestinationPath);

        RequireSupportedNamespace(sourceRootPath, "source directory");
        RequireSupportedNamespace(destinationRootPath, "destination directory");
        EnsureSourceMutationAllowed(sourceRootPath, "source directory");
        EnsureSourceMutationAllowed(sourcePath, "source file");
        cancellationToken.ThrowIfCancellationRequested();

        var sourceLeaf = ValidateDirectChild(
            sourceRootPath,
            sourcePath,
            request.Entry.Name,
            "source");
        var destinationLeaf = ValidateDirectChild(
            destinationRootPath,
            destinationPath,
            request.Entry.Name,
            "destination");

        SafeFileHandle? sourceDirectory = null;
        SafeFileHandle? sourceFile = null;
        SafeFileHandle? destinationDirectory = null;
        SafeFileHandle? destinationFile = null;
        try
        {
            sourceDirectory = OpenDirectoryHandle(sourceRootPath, "cross-volume Move source directory");
            destinationDirectory = OpenDirectoryHandle(destinationRootPath, "cross-volume Move destination directory");
            ValidateDirectoryHandle(
                sourceDirectory,
                sourceRootPath,
                request.SourceDirectoryIdentity,
                "cross-volume Move source directory");
            RequireSupportedSourceDeleteVolume(sourceDirectory);
            ValidateDirectoryHandle(
                destinationDirectory,
                destinationRootPath,
                request.DestinationDirectoryIdentity,
                "cross-volume Move destination directory");
            cancellationToken.ThrowIfCancellationRequested();

            // Hold the committed destination with no write/delete sharing before opening the
            // destructive source capability. This freezes its identity/content namespace for
            // the lifetime of the source-delete lease.
            destinationFile = OpenRelativeFile(
                destinationDirectory,
                destinationLeaf,
                FileReadData | FileReadAttributes | Synchronize,
                FileShare.Read,
                FileOpen,
                FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
                "cross-volume Move committed destination");
            var destinationBefore = ValidateFileHandle(
                destinationFile,
                destinationPath,
                request.DestinationIdentity,
                "cross-volume Move committed destination");
            var actualFingerprint = HashMainStream(destinationFile, cancellationToken);
            var destinationAfter = ValidateFileHandle(
                destinationFile,
                destinationPath,
                request.DestinationIdentity,
                "cross-volume Move committed destination");
            if (FileSize(destinationBefore) != FileSize(destinationAfter) ||
                ToUInt64(destinationBefore.LastWriteTime) != ToUInt64(destinationAfter.LastWriteTime) ||
                actualFingerprint != request.DestinationContentFingerprint)
            {
                throw new IOException(
                    "The committed cross-volume Move destination no longer matches its durable identity/content evidence.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            sourceFile = OpenRelativeFile(
                sourceDirectory,
                sourceLeaf,
                Delete | FileReadAttributes | Synchronize,
                FileShare.Read,
                FileOpen,
                FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
                "cross-volume Move source-delete file");
            ValidateFileHandle(
                sourceFile,
                sourcePath,
                request.SourceIdentity,
                "cross-volume Move source-delete file");

            // Re-check both roots and the destination after the source capability exists.
            ValidateDirectoryHandle(
                sourceDirectory,
                sourceRootPath,
                request.SourceDirectoryIdentity,
                "cross-volume Move source directory");
            RequireSupportedSourceDeleteVolume(sourceDirectory);
            ValidateDirectoryHandle(
                destinationDirectory,
                destinationRootPath,
                request.DestinationDirectoryIdentity,
                "cross-volume Move destination directory");
            ValidateFileHandle(
                destinationFile,
                destinationPath,
                request.DestinationIdentity,
                "cross-volume Move committed destination");
            cancellationToken.ThrowIfCancellationRequested();

            var evidence = new FileCrossVolumeMoveSourceDeleteEvidence(request);
            var lease = new SourceDeleteLease(
                evidence,
                sourceDirectory,
                sourceFile,
                destinationDirectory,
                destinationFile,
                _protectedLocationPolicy);
            sourceDirectory = null;
            sourceFile = null;
            destinationDirectory = null;
            destinationFile = null;
            return lease;
        }
        finally
        {
            DisposeNoThrow(sourceFile);
            DisposeNoThrow(sourceDirectory);
            DisposeNoThrow(destinationFile);
            DisposeNoThrow(destinationDirectory);
        }
    }

    private void RequireSupportedNamespace(string canonicalDirectoryPath, string description)
    {
        var capability = _namespaceCapabilityProbe.QueryDirectory(canonicalDirectoryPath);
        if (!capability.CanUseCurrentMutationModel)
        {
            throw new NotSupportedException(
                $"Cross-volume Move {description} is not supported by the current namespace model: {capability.Summary}");
        }
    }

    private static void RequireSupportedSourceDeleteVolume(SafeFileHandle sourceDirectory)
    {
        var volumeName = new StringBuilder(261);
        var fileSystemName = new StringBuilder(64);
        if (!GetVolumeInformationByHandleW(
                sourceDirectory,
                volumeName,
                checked((uint)volumeName.Capacity),
                out _,
                out _,
                out var fileSystemFlags,
                fileSystemName,
                checked((uint)fileSystemName.Capacity)))
        {
            throw Win32IOException(
                "Reading exact source-volume filesystem capabilities for cross-volume Move source deletion");
        }

        if (!string.Equals(fileSystemName.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Cross-volume Move source deletion requires exact NTFS mutation evidence; the identity-bound source volume reported '{fileSystemName}'.");
        }
        if (!SupportsPosixUnlinkRename(fileSystemFlags))
        {
            throw new NotSupportedException(
                "The exact identity-bound source volume does not advertise FILE_SUPPORTS_POSIX_UNLINK_RENAME required by the reviewed source-delete primitive.");
        }
    }

    private void EnsureSourceMutationAllowed(string canonicalPath, string description)
    {
        var decision = _protectedLocationPolicy.Evaluate(canonicalPath);
        if (decision.IsBlocked)
        {
            throw new UnauthorizedAccessException(
                $"The cross-volume Move {description} is protected from source deletion: {decision.Reason}");
        }
    }

    private static void ValidateRequest(FileCrossVolumeMoveSourceDeleteRequest request)
    {
        if (request.SourceDeleteMutationAuthorized ||
            request.Entry.IsDirectory ||
            request.Ordinal < 0 ||
            request.SourceDirectoryIdentity.VolumeSerialNumber ==
                request.DestinationDirectoryIdentity.VolumeSerialNumber ||
            request.SourceIdentity.VolumeSerialNumber !=
                request.SourceDirectoryIdentity.VolumeSerialNumber ||
            request.DestinationIdentity.VolumeSerialNumber !=
                request.DestinationDirectoryIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Windows cross-volume Move source-delete acquisition requires exact non-authorizing regular-file evidence bound to distinct volumes.",
                nameof(request));
        }
    }

    private static string ValidateDirectChild(
        string expectedRootPath,
        string expectedFilePath,
        string expectedName,
        string description)
    {
        var parent = Path.GetDirectoryName(expectedFilePath)
            ?? throw new InvalidOperationException(
                $"The cross-volume Move {description} file has no canonical parent directory.");
        if (!PathsEqual(parent, expectedRootPath))
        {
            throw new InvalidOperationException(
                $"The cross-volume Move {description} file is no longer a direct child of its canonical root.");
        }

        var leaf = Path.GetFileName(expectedFilePath);
        if (string.IsNullOrWhiteSpace(leaf) ||
            !string.Equals(leaf, expectedName, StringComparison.OrdinalIgnoreCase) ||
            leaf.Contains(Path.DirectorySeparatorChar) ||
            leaf.Contains(Path.AltDirectorySeparatorChar) ||
            leaf.Contains(Path.VolumeSeparatorChar))
        {
            throw new InvalidOperationException(
                $"The cross-volume Move {description} leaf is not the exact safe plan component.");
        }

        return leaf;
    }

    private static SafeFileHandle OpenDirectoryHandle(string canonicalPath, string description)
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
            var exception = Win32IOException($"Opening {description} '{canonicalPath}'");
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
        uint createOptions,
        string description)
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
                    $"NtCreateFile for {description} leaf '{leafName}' failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }
            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            {
                throw new IOException(
                    $"NtCreateFile returned success without a valid handle for {description}.");
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

    private static ByHandleFileInformation ValidateDirectoryHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity,
        string description)
    {
        var information = GetInformation(handle, description);
        if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The {description} changed into an unsafe object.");
        }
        if (!PathsEqual(GetFinalPath(handle, description), expectedCanonicalPath) ||
            ToIdentity(information) != expectedIdentity)
        {
            throw new IOException($"The {description} canonical path or filesystem identity changed.");
        }
        return information;
    }

    private static ByHandleFileInformation ValidateFileHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity,
        string description)
    {
        var information = GetInformation(handle, description);
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The {description} changed type or became a reparse point.");
        }
        if (!PathsEqual(GetFinalPath(handle, description), expectedCanonicalPath) ||
            ToIdentity(information) != expectedIdentity)
        {
            throw new IOException($"The {description} canonical path or filesystem identity changed.");
        }
        return information;
    }

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle, string description)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw Win32IOException("Reading identity for " + description);
        }
        return information;
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
                    throw Win32IOException(
                        "Reading the committed cross-volume Move destination");
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

    private static FileIdentity ToIdentity(ByHandleFileInformation information) =>
        new(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

    private static ulong FileSize(ByHandleFileInformation information) =>
        ((ulong)information.FileSizeHigh << 32) | information.FileSizeLow;

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

    private sealed class SourceDeleteLease : IFileCrossVolumeMoveSourceDeleteLease
    {
        private const uint FileDispositionDelete = 0x00000001;
        private const uint FileDispositionPosixSemantics = 0x00000002;
        private const uint FileDispositionForceImageSectionCheck = 0x00000004;

        private readonly IFileDeleteProtectedLocationPolicy _protectedLocationPolicy;
        private SafeFileHandle? _sourceDirectory;
        private SafeFileHandle? _sourceFile;
        private SafeFileHandle? _destinationDirectory;
        private SafeFileHandle? _destinationFile;
        private int _mutationAttempted;
        private int _mutationPerformed;
        private int _sourceHandleCloseAttempted;
        private int _sourceHandleCloseCompleted;

        public SourceDeleteLease(
            FileCrossVolumeMoveSourceDeleteEvidence evidence,
            SafeFileHandle sourceDirectory,
            SafeFileHandle sourceFile,
            SafeFileHandle destinationDirectory,
            SafeFileHandle destinationFile,
            IFileDeleteProtectedLocationPolicy protectedLocationPolicy)
        {
            Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
            _sourceDirectory = sourceDirectory ?? throw new ArgumentNullException(nameof(sourceDirectory));
            _sourceFile = sourceFile ?? throw new ArgumentNullException(nameof(sourceFile));
            _destinationDirectory = destinationDirectory ?? throw new ArgumentNullException(nameof(destinationDirectory));
            _destinationFile = destinationFile ?? throw new ArgumentNullException(nameof(destinationFile));
            _protectedLocationPolicy = protectedLocationPolicy ??
                throw new ArgumentNullException(nameof(protectedLocationPolicy));
        }

        public FileCrossVolumeMoveSourceDeleteEvidence Evidence { get; }

        public bool DeleteAccessCapabilityHeld =>
            IsLive(_sourceDirectory) && IsLive(_sourceFile) &&
            IsLive(_destinationDirectory) && IsLive(_destinationFile);

        public bool SourceDeleteMutationPerformed => Volatile.Read(ref _mutationPerformed) != 0;

        public bool SourceDeleteHandleCloseCompleted =>
            Volatile.Read(ref _sourceHandleCloseCompleted) != 0;

        public ValueTask MarkDeletePendingAsync(
            FileCrossVolumeMoveSourceDeleteAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            cancellationToken.ThrowIfCancellationRequested();
            if (!authorization.SourceDeleteBarrierSatisfied ||
                !authorization.SourceDeleteMutationAuthorized ||
                !authorization.IsBoundTo(Evidence))
            {
                throw new UnauthorizedAccessException(
                    "Cross-volume Move source deletion requires the exact Core-minted post-barrier authorization for this live lease.");
            }

            var sourceDirectory = Volatile.Read(ref _sourceDirectory);
            var sourceFile = Volatile.Read(ref _sourceFile);
            var destinationDirectory = Volatile.Read(ref _destinationDirectory);
            var destinationFile = Volatile.Read(ref _destinationFile);
            if (!IsLive(sourceDirectory) || !IsLive(sourceFile) ||
                !IsLive(destinationDirectory) || !IsLive(destinationFile))
            {
                throw new ObjectDisposedException(
                    nameof(SourceDeleteLease),
                    "Cross-volume Move source deletion requires all four exact live capability/evidence handles.");
            }

            var sourceDecision = _protectedLocationPolicy.Evaluate(Evidence.CanonicalSourcePath);
            if (sourceDecision.IsBlocked)
            {
                throw new UnauthorizedAccessException(
                    $"The cross-volume Move source became protected before mutation: {sourceDecision.Reason}");
            }

            ValidateDirectoryHandle(
                sourceDirectory!,
                Evidence.CanonicalSourceDirectoryPath,
                Evidence.SourceDirectoryIdentity,
                "cross-volume Move source directory");
            ValidateFileHandle(
                sourceFile!,
                Evidence.CanonicalSourcePath,
                Evidence.SourceIdentity,
                "cross-volume Move source-delete file");
            ValidateDirectoryHandle(
                destinationDirectory!,
                Evidence.CanonicalDestinationDirectoryPath,
                Evidence.DestinationDirectoryIdentity,
                "cross-volume Move destination directory");
            ValidateFileHandle(
                destinationFile!,
                Evidence.CanonicalDestinationPath,
                Evidence.DestinationIdentity,
                "cross-volume Move committed destination");
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.CompareExchange(ref _mutationAttempted, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Cross-volume Move source delete disposition may be attempted only once per capability lease.");
            }

            var disposition = new FileDispositionInformationEx
            {
                Flags =
                    FileDispositionDelete |
                    FileDispositionPosixSemantics |
                    FileDispositionForceImageSectionCheck,
            };
            var status = NtSetInformationFile(
                sourceFile!,
                out _,
                ref disposition,
                checked((uint)Marshal.SizeOf<FileDispositionInformationEx>()),
                FileInformationClass.FileDispositionInformationEx);
            if (status < 0)
            {
                throw new IOException(
                    $"NtSetInformationFile(FileDispositionInformationEx) for the exact cross-volume Move source handle failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }

            Volatile.Write(ref _mutationPerformed, 1);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseSourceDeleteHandleAsync(
            FileCrossVolumeMoveSourceDeleteAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            cancellationToken.ThrowIfCancellationRequested();
            if (!authorization.SourceDeleteBarrierSatisfied ||
                !authorization.SourceDeleteMutationAuthorized ||
                !authorization.IsBoundTo(Evidence))
            {
                throw new UnauthorizedAccessException(
                    "Cross-volume Move checked source-handle close requires the exact Core-minted post-barrier authority for this lease.");
            }
            if (!SourceDeleteMutationPerformed)
            {
                throw new InvalidOperationException(
                    "Cross-volume Move cannot close the destructive source handle before exact source disposition is reported.");
            }
            if (Interlocked.CompareExchange(ref _sourceHandleCloseAttempted, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Cross-volume Move destructive source handle close may be attempted only once per capability lease.");
            }

            var sourceFile = Volatile.Read(ref _sourceFile);
            var sourceDirectory = Volatile.Read(ref _sourceDirectory);
            var destinationDirectory = Volatile.Read(ref _destinationDirectory);
            var destinationFile = Volatile.Read(ref _destinationFile);
            if (!IsLive(sourceFile) || !IsLive(sourceDirectory) ||
                !IsLive(destinationDirectory) || !IsLive(destinationFile))
            {
                throw new ObjectDisposedException(
                    nameof(SourceDeleteLease),
                    "Cross-volume Move checked source-handle close requires the exact source handle and retained identity evidence handles to remain live.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var status = NtClose(sourceFile!.DangerousGetHandle());
            if (status < 0)
            {
                throw new IOException(
                    $"NtClose for the exact cross-volume Move POSIX source-delete handle failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }

            sourceFile.SetHandleAsInvalid();
            _sourceFile = null;
            Volatile.Write(ref _sourceHandleCloseCompleted, 1);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            // A successful checked close already detached the exact source file handle. If
            // checked close failed or was never attempted, cleanup may still try best effort,
            // but cleanup is never accepted as the positive destructive completion receipt.
            DisposeNoThrow(_sourceFile);
            _sourceFile = null;
            DisposeNoThrow(_sourceDirectory);
            _sourceDirectory = null;
            DisposeNoThrow(_destinationFile);
            _destinationFile = null;
            DisposeNoThrow(_destinationDirectory);
            _destinationDirectory = null;
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

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        ref FileDispositionInformationEx fileInformation,
        uint length,
        FileInformationClass fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern int NtClose(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    private enum FileInformationClass
    {
        FileDispositionInformationEx = 64,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformationEx
    {
        public uint Flags;
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
