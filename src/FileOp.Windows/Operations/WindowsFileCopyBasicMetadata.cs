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
    private const uint FileAttributeTemporary = 0x00000100;
    private const uint FileAttributeOffline = 0x00001000;
    private const uint FileAttributeNotContentIndexed = 0x00002000;

    private const uint PreservedAttributeMask =
        FileAttributeReadOnly |
        FileAttributeHidden |
        FileAttributeSystem |
        FileAttributeArchive |
        FileAttributeNotContentIndexed;

    // MS-FSA FileBasicInformation permits TEMPORARY and OFFLINE to be changed
    // through the same basic-information operation. They are destination-owned
    // for Copy, so preserve their current destination values when overlaying the
    // source fidelity subset. Compression, sparse, encryption and integrity state
    // are not in the FileBasicInformation valid-set mask and are left untouched by
    // the filesystem without carrying those flags through the input structure.
    private const uint DestinationOwnedSettableAttributeMask =
        FileAttributeTemporary |
        FileAttributeOffline;

    internal static Snapshot Capture(SafeFileHandle sourceHandle)
    {
        ValidateHandle(sourceHandle, "source", nameof(sourceHandle));
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

    /// <summary>
    /// Prevents later I/O through the destination handle from scheduling automatic
    /// last-access/last-write timestamp updates. The explicit source timestamps are
    /// applied after the byte copy without re-enabling those automatic updates.
    /// </summary>
    internal static void SuppressAutomaticTimestampUpdates(SafeFileHandle destinationHandle)
    {
        ValidateHandle(destinationHandle, "destination", nameof(destinationHandle));
        var information = new FileBasicInformation
        {
            CreationTime = 0,
            LastAccessTime = -1,
            LastWriteTime = -1,
            ChangeTime = 0,
            FileAttributes = 0,
        };
        SetBasicInformation(
            destinationHandle,
            ref information,
            "Suppressing automatic destination Copy timestamp updates");
    }

    internal static void Apply(SafeFileHandle destinationHandle, Snapshot snapshot)
    {
        ValidateHandle(destinationHandle, "destination", nameof(destinationHandle));
        if (!GetFileInformationByHandle(destinationHandle, out var destinationInformation))
        {
            throw Win32IOException("Reading destination Copy metadata before apply");
        }

        var information = new FileBasicInformation
        {
            CreationTime = snapshot.CreationTime,
            LastAccessTime = snapshot.LastAccessTime,
            LastWriteTime = snapshot.LastWriteTime,
            // Zero leaves destination change-time ownership with the filesystem.
            ChangeTime = 0,
            FileAttributes = MergeDestinationAttributes(
                destinationInformation.FileAttributes,
                snapshot.FileAttributes),
        };
        SetBasicInformation(
            destinationHandle,
            ref information,
            "Applying destination Copy metadata");
    }

    internal static uint MergeDestinationAttributes(
        uint destinationAttributes,
        uint sourceAttributes)
    {
        var destinationOwned = destinationAttributes & DestinationOwnedSettableAttributeMask;
        var sourcePreserved = sourceAttributes & PreservedAttributeMask;
        var merged = destinationOwned | sourcePreserved;
        return merged == 0 ? FileAttributeNormal : merged;
    }

    private static void SetBasicInformation(
        SafeFileHandle handle,
        ref FileBasicInformation information,
        string action)
    {
        if (!SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileBasicInfo,
                ref information,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw Win32IOException(action);
        }
    }

    private static void ValidateHandle(
        SafeFileHandle handle,
        string description,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException(
                $"The {description} metadata handle must be open and valid.",
                parameterName);
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

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
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
