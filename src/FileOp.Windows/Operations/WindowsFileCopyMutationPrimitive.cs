using System;
using System.Buffers;
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
/// Performs one file Copy using handles that remain alive until the executor has
/// durably committed and reported the mutation lease.
/// </summary>
public sealed class WindowsFileCopyMutationPrimitive : IFileCopyMutationPrimitive
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
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
    private const uint FileCreate = 2;
    private const ulong FileCreated = 2;
    private const uint ObjCaseInsensitive = 0x00000040;

    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const int CopyBufferSize = 1024 * 1024;

    public ValueTask<IFileCopyMutationLease> CopyNewFileAsync(
        FileOperationExecutionValidationItem validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ValidateInput(validation);
        return new ValueTask<IFileCopyMutationLease>(
            Task.Run(() => CopyNewFile(validation)));
    }

    private static IFileCopyMutationLease CopyNewFile(
        FileOperationExecutionValidationItem validation)
    {
        var rootBinding = validation.MutationRootBinding
            ?? throw new InvalidOperationException(
                "The validation item is not bound to canonical source/destination roots.");
        var expectedSourceDirectoryIdentity = rootBinding.SourceDirectoryIdentity
            ?? throw new InvalidOperationException("The validated source directory has no stable identity.");
        var expectedDestinationDirectoryIdentity = rootBinding.DestinationDirectoryIdentity
            ?? throw new InvalidOperationException("The validated destination directory has no stable identity.");

        var sourceCanonicalPath = NormalizeForComparison(validation.Source.CanonicalPath);
        var destinationCanonicalPath = NormalizeForComparison(validation.Destination.CanonicalPath);
        var sourceParentPath = Path.GetDirectoryName(sourceCanonicalPath)
            ?? throw new InvalidOperationException("The validated source has no canonical parent directory.");
        var destinationParentPath = Path.GetDirectoryName(destinationCanonicalPath)
            ?? throw new InvalidOperationException("The validated destination has no canonical parent directory.");
        if (!PathsEqual(sourceParentPath, rootBinding.CanonicalSourceDirectoryPath) ||
            !PathsEqual(destinationParentPath, rootBinding.CanonicalDestinationDirectoryPath))
        {
            throw new InvalidOperationException(
                "The validation item's canonical leaf parents do not match its bound canonical roots.");
        }

        var sourceLeafName = Path.GetFileName(sourceCanonicalPath);
        var destinationLeafName = Path.GetFileName(destinationCanonicalPath);
        var expectedSourceIdentity = validation.Source.Identity
            ?? throw new InvalidOperationException("The validated source file has no stable identity.");

        SafeFileHandle? sourceDirectory = null;
        SafeFileHandle? destinationDirectory = null;
        SafeFileHandle? sourceFile = null;
        SafeFileHandle? destinationFile = null;
        try
        {
            sourceDirectory = OpenDirectoryHandle(sourceParentPath);
            ValidateDirectoryHandle(
                sourceDirectory,
                sourceParentPath,
                expectedSourceDirectoryIdentity,
                "source");

            destinationDirectory = OpenDirectoryHandle(destinationParentPath);
            ValidateDirectoryHandle(
                destinationDirectory,
                destinationParentPath,
                expectedDestinationDirectoryIdentity,
                "destination");

            sourceFile = OpenRelativeFile(
                sourceDirectory,
                sourceLeafName,
                GenericRead | FileReadAttributes | Synchronize,
                FileShare.Read,
                FileOpen,
                FileSequentialOnly | FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
                out _);
            var sourceIdentity = ValidateFileHandle(
                sourceFile,
                sourceCanonicalPath,
                expectedSourceIdentity,
                "source");

            destinationFile = OpenRelativeFile(
                destinationDirectory,
                destinationLeafName,
                GenericWrite | FileReadAttributes | Synchronize,
                FileShare.Read,
                FileCreate,
                FileSequentialOnly | FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
                out var createInformation);
            if (createInformation != FileCreated)
            {
                throw new IOException(
                    $"Exclusive destination creation returned unexpected information value {createInformation}.");
            }

            CopyContents(sourceFile, destinationFile);
            if (!FlushFileBuffers(destinationFile))
            {
                throw Win32IOException("Flushing copied destination data");
            }

            var destinationIdentity = ValidateCreatedFileHandle(
                destinationFile,
                destinationCanonicalPath,
                sourceIdentity);
            var receipt = new FileCopyMutationReceipt(
                sourceCanonicalPath,
                destinationCanonicalPath,
                sourceIdentity,
                destinationIdentity);

            var lease = new MutationLease(
                receipt,
                sourceDirectory,
                destinationDirectory,
                sourceFile,
                destinationFile);
            sourceDirectory = null;
            destinationDirectory = null;
            sourceFile = null;
            destinationFile = null;
            return lease;
        }
        finally
        {
            DisposeNoThrow(destinationFile);
            DisposeNoThrow(sourceFile);
            DisposeNoThrow(destinationDirectory);
            DisposeNoThrow(sourceDirectory);
        }
    }

    private static void ValidateInput(FileOperationExecutionValidationItem validation)
    {
        if (validation.Entry.IsDirectory ||
            validation.Decision != FileOperationExecutionValidationDecision.Ready ||
            validation.Source.State != FileOperationCanonicalPathState.File ||
            validation.Source.IsLeafReparsePoint ||
            !validation.Source.Identity.HasValue ||
            validation.Destination.State != FileOperationCanonicalPathState.Missing ||
            validation.Destination.Identity.HasValue ||
            string.IsNullOrWhiteSpace(validation.Source.CanonicalPath) ||
            string.IsNullOrWhiteSpace(validation.Destination.CanonicalPath) ||
            validation.MutationRootBinding is not
            {
                SourceDirectoryIdentity: not null,
                DestinationDirectoryIdentity: not null,
            })
        {
            throw new ArgumentException(
                "Windows Copy mutation requires one freshly validated ready file bound to stable canonical source/destination roots.",
                nameof(validation));
        }

        var sourceLeaf = Path.GetFileName(validation.Source.CanonicalPath);
        var destinationLeaf = Path.GetFileName(validation.Destination.CanonicalPath);
        if (string.IsNullOrWhiteSpace(sourceLeaf) ||
            string.IsNullOrWhiteSpace(destinationLeaf) ||
            !string.Equals(sourceLeaf, validation.Entry.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(destinationLeaf, validation.Entry.Name, StringComparison.OrdinalIgnoreCase) ||
            sourceLeaf.Contains(Path.VolumeSeparatorChar) ||
            destinationLeaf.Contains(Path.VolumeSeparatorChar))
        {
            throw new ArgumentException(
                "Windows Copy mutation requires the validated source/destination leaf name to match the immutable entry name.",
                nameof(validation));
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
            var exception = Win32IOException($"Opening canonical directory '{canonicalPath}'");
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
        var information = GetInformation(handle, description + " directory");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"The {description} canonical parent changed into an unsafe directory object before Copy.");
        }

        var actualPath = GetFinalPath(handle, description + " directory");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The {description} canonical parent changed before Copy. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        if (ToIdentity(information) != expectedIdentity)
        {
            throw new IOException(
                $"The {description} canonical parent identity changed before Copy.");
        }
    }

    private static FileIdentity ValidateFileHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity expectedIdentity,
        string description)
    {
        var information = GetInformation(handle, description + " file");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The validated {description} changed type before Copy.");
        }

        var actualPath = GetFinalPath(handle, description + " file");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The validated {description} canonical path changed before Copy. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        var actualIdentity = ToIdentity(information);
        if (actualIdentity != expectedIdentity)
        {
            throw new IOException(
                $"The validated {description} filesystem identity changed before Copy.");
        }

        return actualIdentity;
    }

    private static FileIdentity ValidateCreatedFileHandle(
        SafeFileHandle handle,
        string expectedCanonicalPath,
        FileIdentity sourceIdentity)
    {
        var information = GetInformation(handle, "created destination file");
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The exclusively created destination is not an ordinary file.");
        }

        var actualPath = GetFinalPath(handle, "created destination file");
        if (!PathsEqual(actualPath, expectedCanonicalPath))
        {
            throw new IOException(
                $"The created destination canonical path is unexpected. Expected '{expectedCanonicalPath}', got '{actualPath}'.");
        }

        var identity = ToIdentity(information);
        if (identity == sourceIdentity)
        {
            throw new IOException("The created destination unexpectedly has the same filesystem identity as the source.");
        }

        return identity;
    }

    private static SafeFileHandle OpenRelativeFile(
        SafeFileHandle rootDirectory,
        string leafName,
        uint desiredAccess,
        FileShare shareAccess,
        uint createDisposition,
        uint createOptions,
        out ulong createInformation)
    {
        if (string.IsNullOrWhiteSpace(leafName) ||
            leafName.Contains(Path.DirectorySeparatorChar) ||
            leafName.Contains(Path.AltDirectorySeparatorChar) ||
            leafName.Contains(Path.VolumeSeparatorChar))
        {
            throw new ArgumentException("A relative Copy leaf must contain only one path component.", nameof(leafName));
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
                out var ioStatus,
                IntPtr.Zero,
                FileAttributeNormal,
                (uint)shareAccess,
                createDisposition,
                createOptions,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                if (status == StatusObjectNameCollision)
                {
                    throw new IOException(
                        $"The destination leaf '{leafName}' appeared before exclusive creation could complete.");
                }

                throw new IOException(
                    $"NtCreateFile for relative leaf '{leafName}' failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }

            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            {
                throw new IOException("NtCreateFile returned success without a valid file handle.");
            }

            createInformation = ioStatus.Information.ToUInt64();
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

    private static void CopyContents(SafeFileHandle source, SafeFileHandle destination)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var bufferPointer = pinned.AddrOfPinnedObject();
            while (true)
            {
                if (!ReadFile(
                        source,
                        bufferPointer,
                        checked((uint)Math.Min(buffer.Length, CopyBufferSize)),
                        out var bytesRead,
                        IntPtr.Zero))
                {
                    throw Win32IOException("Reading the source file");
                }

                if (bytesRead == 0)
                {
                    break;
                }

                uint written = 0;
                while (written < bytesRead)
                {
                    if (!WriteFile(
                            destination,
                            IntPtr.Add(bufferPointer, checked((int)written)),
                            bytesRead - written,
                            out var justWritten,
                            IntPtr.Zero))
                    {
                        throw Win32IOException("Writing the destination file");
                    }

                    if (justWritten == 0)
                    {
                        throw new IOException("Writing the destination file made no forward progress.");
                    }

                    written = checked(written + justWritten);
                }
            }
        }
        finally
        {
            pinned.Free();
            ArrayPool<byte>.Shared.Return(buffer);
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

        throw new IOException("Resolving the final handle path exceeded the supported buffer growth attempts.");
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

    private sealed class MutationLease : IFileCopyMutationLease
    {
        private SafeFileHandle? _sourceDirectory;
        private SafeFileHandle? _destinationDirectory;
        private SafeFileHandle? _sourceFile;
        private SafeFileHandle? _destinationFile;

        public MutationLease(
            FileCopyMutationReceipt receipt,
            SafeFileHandle sourceDirectory,
            SafeFileHandle destinationDirectory,
            SafeFileHandle sourceFile,
            SafeFileHandle destinationFile)
        {
            Receipt = receipt;
            _sourceDirectory = sourceDirectory;
            _destinationDirectory = destinationDirectory;
            _sourceFile = sourceFile;
            _destinationFile = destinationFile;
        }

        public FileCopyMutationReceipt Receipt { get; }

        public ValueTask DisposeAsync()
        {
            DisposeNoThrow(_destinationFile);
            _destinationFile = null;
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
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

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