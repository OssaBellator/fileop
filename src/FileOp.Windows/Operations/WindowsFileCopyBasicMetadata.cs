using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

/// <summary>
/// Captures and reapplies the deliberately narrow basic-metadata subset that
/// FileOp can preserve through already-bound file handles without reopening a
/// textual filesystem path.
/// </summary>
internal static class WindowsFileCopyBasicMetadata
{
    private const uint FileAttributeReadOnly = 0x00000001;
    private const uint FileAttributeHidden = 0x00000002;
    private const uint FileAttributeSystem = 0x00000004;
    private const uint FileAttributeArchive = 0x00000020;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeNotContentIndexed = 0x00002000;

    private const uint PreservedAttributeMask =
        FileAttributeReadOnly |
        FileAttributeHidden |
        FileAttributeSystem |
        FileAttributeArchive |
        FileAttributeNotContentIndexed;

    internal static Snapshot Capture(SafeFileHandle sourceHandle)
    {
        ArgumentNullException.ThrowIfNull(sourceHandle);
        if (sourceHandle.IsInvalid || sourceHandle.IsClosed)
        {
            throw new ArgumentException("The source metadata handle must be open and valid.", nameof(sourceHandle));
        }

        if (!GetFileInformationByHandle(sourceHandle, out var information))
        {
            throw Win32IOException("Reading source Copy metadata");
        }

        if ((information.FileAttributes & (uint)FileAttributes.Directory) != 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Copy basic metadata can only be captured from an ordinary file handle.");
        }

        return new Snapshot(
            FileTimeToInt64(information.CreationTime),
            FileTimeToInt64(information.LastAccessTime),
            FileTimeToInt64(information.LastWriteTime),
            SanitizeAttributes(information.FileAttributes));
    }

    internal static void Apply(SafeFileHandle destinationHandle, Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(destinationHandle);
        if (destinationHandle.IsInvalid || destinationHandle.IsClosed)
        {
            throw new ArgumentException(
                "The destination metadata handle must be open and valid.",
                nameof(destinationHandle));
        }

        var information = new FileBasicInformation
        {
            CreationTime = snapshot.CreationTime,
            LastAccessTime = snapshot.LastAccessTime,
            LastWriteTime = snapshot.LastWriteTime,
            // Zero asks Windows to leave the destination change-time field alone.
            ChangeTime = 0,
            FileAttributes = snapshot.FileAttributes,
        };

        if (!SetFileInformationByHandle(
                destinationHandle,
                FileInfoByHandleClass.FileBasicInfo,
                ref information,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw Win32IOException("Applying destination Copy metadata");
        }
    }

    private static uint SanitizeAttributes(uint sourceAttributes)
    {
        var preserved = sourceAttributes & PreservedAttributeMask;
        return preserved == 0 ? FileAttributeNormal : preserved;
    }

    private static long FileTimeToInt64(FileTime value) =>
        ((long)value.HighDateTime << 32) | value.LowDateTime;

    private static IOException Win32IOException(string action)
    {
        var error = Marshal.GetLastWin32Error();
        return new IOException(
            $"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}");
    }

    internal readonly record struct Snapshot(
        long CreationTime,
        long LastAccessTime,
        long LastWriteTime,
        uint FileAttributes);

    private enum FileInfoByHandleClass
    {
        FileBasicInfo = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        FileInfoByHandleClass fileInformationClass,
        ref FileBasicInformation lpFileInformation,
        uint dwBufferSize);
}
