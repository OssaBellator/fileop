using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FileOp.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

internal enum FileOperationVolumeRelationshipState
{
    SameVolume,
    DifferentVolume,
    Unavailable,
}

internal sealed record FileOperationVolumeRelationship(
    FileOperationVolumeRelationshipState State,
    string? SourceVolumeGuidName,
    string? DestinationVolumeGuidName,
    string Summary)
{
    public bool IsSameVolume => State == FileOperationVolumeRelationshipState.SameVolume;
}

internal interface IFileOperationVolumeRelationshipProbe
{
    FileOperationVolumeRelationship Query(
        string canonicalSourceDirectoryPath,
        FileIdentity expectedSourceIdentity,
        string canonicalDestinationDirectoryPath,
        FileIdentity expectedDestinationIdentity);
}

/// <summary>
/// Uses exact identity-bound root handles plus Windows volume-GUID names as the stronger
/// relationship proof for Move roots whose 32-bit volume serials are equal. Drive letters,
/// mount-point text and a newly opened path that no longer has the validated root identity
/// are not treated as volume authority.
/// </summary>
internal sealed class WindowsFileOperationVolumeRelationshipProbe :
    IFileOperationVolumeRelationshipProbe
{
    private const uint FileReadAttributes = 0x00000080u;
    private const uint FileFlagBackupSemantics = 0x02000000u;
    private const uint FileFlagOpenReparsePoint = 0x00200000u;
    private const uint VolumeNameGuid = 0x00000001u;

    public FileOperationVolumeRelationship Query(
        string canonicalSourceDirectoryPath,
        FileIdentity expectedSourceIdentity,
        string canonicalDestinationDirectoryPath,
        FileIdentity expectedDestinationIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDestinationDirectoryPath);

        try
        {
            var source = ResolveVolumeGuidName(
                canonicalSourceDirectoryPath,
                expectedSourceIdentity,
                "source");
            var destination = ResolveVolumeGuidName(
                canonicalDestinationDirectoryPath,
                expectedDestinationIdentity,
                "destination");
            var same = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
            return new FileOperationVolumeRelationship(
                same
                    ? FileOperationVolumeRelationshipState.SameVolume
                    : FileOperationVolumeRelationshipState.DifferentVolume,
                source,
                destination,
                same
                    ? "Validated Move root identities resolve to the same handle-bound Windows volume GUID."
                    : "Validated Move root identities have equal serial evidence but resolve to different handle-bound Windows volume GUIDs.");
        }
        catch (Exception exception) when (
            exception is IOException or Win32Exception or NotSupportedException)
        {
            return new FileOperationVolumeRelationship(
                FileOperationVolumeRelationshipState.Unavailable,
                SourceVolumeGuidName: null,
                DestinationVolumeGuidName: null,
                "Windows could not obtain a stronger identity-bound volume GUID relationship for the validated Move roots: " +
                exception.Message);
        }
    }

    private static string ResolveVolumeGuidName(
        string canonicalDirectoryPath,
        FileIdentity expectedIdentity,
        string description)
    {
        using var handle = CreateFileW(
            canonicalDirectoryPath,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Opening canonical Move {description} root '{canonicalDirectoryPath}' for volume identity");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Reading canonical Move {description} root identity '{canonicalDirectoryPath}'");
        }

        if ((information.FileAttributes & (uint)FileAttributes.Directory) == 0 ||
            (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"The canonical Move {description} root changed into an unsupported object before volume relationship proof.");
        }

        var observedIdentity = new FileIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        if (observedIdentity != expectedIdentity)
        {
            throw new IOException(
                $"The canonical Move {description} root filesystem identity changed before volume relationship proof.");
        }

        var capacity = 512;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                VolumeNameGuid);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Resolving volume GUID for canonical Move {description} root '{canonicalDirectoryPath}'");
            }
            if (length < buffer.Capacity)
            {
                return ExtractVolumeGuidName(buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }

        throw new IOException(
            $"Resolving volume GUID for canonical Move {description} root '{canonicalDirectoryPath}' exceeded the supported path buffer growth limit.");
    }

    private static string ExtractVolumeGuidName(string finalPath)
    {
        const string prefix = @"\\?\Volume{";
        if (!finalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Windows returned a non-volume-GUID final path '{finalPath}'.");
        }

        var terminator = finalPath.IndexOf("}\\", prefix.Length, StringComparison.Ordinal);
        if (terminator < 0)
        {
            throw new NotSupportedException(
                $"Windows returned a malformed volume-GUID final path '{finalPath}'.");
        }

        return finalPath[..checked(terminator + 2)];
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
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

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
