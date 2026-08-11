using System.ComponentModel;
using System.Runtime.InteropServices;
using FileOp.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Storage;

public sealed class WindowsCurrentFilePhysicalEvidenceReader
{
    public StoragePhysicalFileEvidence Read(FileStream stream, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Current physical reclaim evidence is available only on Windows.");
        }
        if (stream.SafeFileHandle.IsInvalid || stream.SafeFileHandle.IsClosed)
        {
            throw new ObjectDisposedException(nameof(stream));
        }

        var fullPath = Path.GetFullPath(path);
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var handleInfo))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not read current file identity/link evidence for {fullPath}.");
        }

        if (!GetFileInformationByHandleEx(
                stream.SafeFileHandle,
                FileInfoByHandleClass.FileStandardInfo,
                out var standardInfo,
                (uint)Marshal.SizeOf<FileStandardInfo>()))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not read current file allocation/link evidence for {fullPath}.");
        }

        if (handleInfo.NumberOfLinks == 0 || standardInfo.NumberOfLinks == 0)
        {
            throw new InvalidDataException(
                $"Windows reported zero hard links for the open file {fullPath}.");
        }
        if (handleInfo.NumberOfLinks != standardInfo.NumberOfLinks)
        {
            throw new InvalidDataException(
                $"Windows returned inconsistent hard-link counts for the open file {fullPath}.");
        }
        if (standardInfo.DeletePending)
        {
            throw new InvalidDataException(
                $"The open file {fullPath} is pending deletion, so reclaim evidence was not trusted.");
        }
        if (standardInfo.Directory)
        {
            throw new InvalidDataException(
                $"The open handle for {fullPath} represents a directory rather than a file.");
        }

        var logicalBytes = CombineUnsigned(handleInfo.FileSizeHigh, handleInfo.FileSizeLow);
        if (logicalBytes > long.MaxValue ||
            standardInfo.EndOfFile < 0 ||
            (ulong)standardInfo.EndOfFile != logicalBytes ||
            standardInfo.EndOfFile != stream.Length)
        {
            throw new InvalidDataException(
                $"The open file {fullPath} changed logical length while physical reclaim evidence was being captured.");
        }
        if (standardInfo.AllocationSize < 0)
        {
            throw new InvalidDataException(
                $"Windows reported a negative allocated size for the open file {fullPath}.");
        }

        return new StoragePhysicalFileEvidence(
            fullPath,
            new StoragePhysicalFileIdentity(
                handleInfo.VolumeSerialNumber,
                CombineUnsigned(handleInfo.FileIndexHigh, handleInfo.FileIndexLow)),
            standardInfo.NumberOfLinks,
            standardInfo.AllocationSize);
    }

    private static ulong CombineUnsigned(uint high, uint low) =>
        ((ulong)high << 32) | low;

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
        FileStandardInfo = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)]
        public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)]
        public bool Directory;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        FileInfoByHandleClass fileInformationClass,
        out FileStandardInfo lpFileInformation,
        uint dwBufferSize);
}
