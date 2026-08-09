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
/// Reads destination metadata at the durable Copy commit boundary. The root is
/// opened and identity-validated first; the leaf is opened relative to that root.
/// No file data is read and no target write/delete access is requested.
/// </summary>
public sealed class WindowsRootBoundFileHardLinkEvidenceSource : IFileCopyDestinationHardLinkEvidenceSource
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

    public ValueTask<uint> ReadVerifiedHardLinkCountAsync(
        FileOperationActionHistory history,
        int ordinal,
        FileIdentity expectedDestinationIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<uint>(Task.Run(
            () => Read(history, ordinal, expectedDestinationIdentity, cancellationToken),
            cancellationToken));
    }

    private static uint Read(
        FileOperationActionHistory history,
        int ordinal,
        FileIdentity expectedDestinationIdentity,
        CancellationToken cancellationToken)
    {
        if (history.Kind != FileOperationKind.Copy ||
            history.DestinationDirectoryIdentity is not FileIdentity expectedRootIdentity ||
            ordinal < 0 ||
            ordinal >= history.Entries.Count ||
            history.Entries[ordinal].Ordinal != ordinal ||
            history.Entries[ordinal].State != FileOperationActionEntryState.MutationStarted)
        {
            throw new InvalidOperationException(
                "Hard-link evidence requires an active MutationStarted Copy entry with durable destination-root identity.");
        }

        var entry = history.Entries[ordinal];
        var rootPath = NormalizeForComparison(history.CanonicalDestinationDirectoryPath);
        var leafPath = NormalizeForComparison(entry.CanonicalDestinationPath);
        var parentPath = Path.GetDirectoryName(leafPath);
        var leafName = Path.GetFileName(leafPath);
        if (string.IsNullOrWhiteSpace(parentPath) ||
            string.IsNullOrWhiteSpace(leafName) ||
            !PathsEqual(parentPath, rootPath) ||
            leafName.Contains(Path.DirectorySeparatorChar) ||
            leafName.Contains(Path.AltDirectorySeparatorChar) ||
            leafName.Contains(Path.VolumeSeparatorChar))
        {
            throw new InvalidOperationException(
                "The recorded Copy destination is not one direct child beneath its recorded destination root.");
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
        ThrowIfInvalid(rootHandle, "Opening the recorded destination root for hard-link evidence");
        var rootBefore = GetInformation(rootHandle, "Reading destination-root identity for hard-link evidence");
        VerifyRoot(rootHandle, rootPath, expectedRootIdentity, rootBefore);

        cancellationToken.ThrowIfCancellationRequested();
        using var leafHandle = OpenRelativeLeaf(rootHandle, leafName);
        var leafBefore = GetInformation(leafHandle, "Reading destination hard-link evidence");
        VerifyLeaf(leafHandle, leafPath, expectedDestinationIdentity, leafBefore);
        if (leafBefore.NumberOfLinks == 0)
        {
            throw new InvalidDataException("The destination leaf reported a zero hard-link count.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var leafAfter = GetInformation(leafHandle, "Rechecking destination hard-link evidence");
        VerifyLeaf(leafHandle, leafPath, expectedDestinationIdentity, leafAfter);
        if (leafAfter.NumberOfLinks == 0 || leafAfter.NumberOfLinks != leafBefore.NumberOfLinks)
        {
            throw new IOException(
                "The destination hard-link count changed while commit-bound topology evidence was being collected.");
        }

        var rootAfter = GetInformation(rootHandle, "Rechecking destination-root identity for hard-link evidence");
        VerifyRoot(rootHandle, rootPath, expectedRootIdentity, rootAfter);
        return leafAfter.NumberOfLinks;
    }

    private static void VerifyRoot(
        SafeFileHandle handle,
        string expectedPath,
        FileIdentity expectedIdentity,
        ByHandleFileInformation information)
    {
        if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ToIdentity(information) != expectedIdentity ||
            !PathsEqual(GetFinalPath(handle), expectedPath))
        {
            throw new IOException(
                "The destination-root handle no longer matches the recorded ordinary-directory path and FileIdentity.");
        }
    }

    private static void VerifyLeaf(
        SafeFileHandle handle,
        string expectedPath,
        FileIdentity expectedIdentity,
        ByHandleFileInformation information)
    {
        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ToIdentity(information) != expectedIdentity ||
            !PathsEqual(GetFinalPath(handle), expectedPath))
        {
            throw new IOException(
                "The destination-leaf handle no longer matches the recorded ordinary-file path and FileIdentity.");
        }
    }

    private static SafeFileHandle OpenRelativeLeaf(SafeFileHandle rootDirectory, string leafName)
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
                FileReadAttributes | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                (uint)FileShare.ReadWrite,
                FileOpen,
                FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw new IOException(
                    $"Opening the destination leaf relative to its verified root failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }

            if (rawHandle == IntPtr.Zero || rawHandle == new IntPtr(-1))
            {
                throw new IOException("NtCreateFile reported success without a valid destination-leaf handle.");
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

    private static ByHandleFileInformation GetInformation(SafeFileHandle handle, string action)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw Win32IOException(action);
        }

        return information;
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
                throw Win32IOException("Reading final path for hard-link evidence");
            }

            if (length < buffer.Capacity)
            {
                return NormalizeFinalPath(buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }

        throw new IOException("The final path exceeded the hard-link evidence buffer growth limit.");
    }

    private static void ThrowIfInvalid(SafeFileHandle handle, string action)
    {
        if (handle.IsInvalid)
        {
            throw Win32IOException(action);
        }
    }

    private static FileIdentity ToIdentity(ByHandleFileInformation information) =>
        new(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

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
        string.Equals(NormalizeForComparison(left), NormalizeForComparison(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForComparison(string path)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode, IntPtr lpSecurityAttributes, FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

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
    private struct FileTime { public uint LowDateTime; public uint HighDateTime; }
}
