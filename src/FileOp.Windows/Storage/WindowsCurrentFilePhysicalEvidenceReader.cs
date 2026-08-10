using System.ComponentModel;
using System.Runtime.InteropServices;
using FileOp.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Storage;

public sealed class WindowsCurrentFilePhysicalEvidenceReader
{
    private const uint InvalidFileSize = uint.MaxValue;

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

        if (handleInfo.NumberOfLinks == 0)
        {
            throw new InvalidDataException(
                $"Windows reported zero hard links for the open file {fullPath}.");
        }

        var logicalBytes = CombineUnsigned(handleInfo.FileSizeHigh, handleInfo.FileSizeLow);
        if (logicalBytes > long.MaxValue || (long)logicalBytes != stream.Length)
        {
            throw new InvalidDataException(
                $"The open file {fullPath} changed logical length while physical reclaim evidence was being captured.");
        }

        Marshal.SetLastPInvokeError(0);
        var allocationLow = GetCompressedFileSizeW(
            ToExtendedLengthPath(fullPath),
            out var allocationHigh);
        var allocationError = Marshal.GetLastPInvokeError();
        if (allocationLow == InvalidFileSize && allocationError != 0)
        {
            throw new Win32Exception(
                allocationError,
                $"Could not read current allocated disk bytes for {fullPath}.");
        }

        var allocatedBytes = CombineUnsigned(allocationHigh, allocationLow);
        if (allocatedBytes > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Windows reported an allocated size outside FileOp's signed-byte range for {fullPath}.");
        }

        return new StoragePhysicalFileEvidence(
            fullPath,
            new StoragePhysicalFileIdentity(
                handleInfo.VolumeSerialNumber,
                CombineUnsigned(handleInfo.FileIndexHigh, handleInfo.FileIndexLow)),
            handleInfo.NumberOfLinks,
            (long)allocatedBytes);
    }

    private static ulong CombineUnsigned(uint high, uint low) =>
        ((ulong)high << 32) | low;

    private static string ToExtendedLengthPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath[2..];
        }

        return @"\\?\" + fullPath;
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

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(
        string lpFileName,
        out uint lpFileSizeHigh);
}
