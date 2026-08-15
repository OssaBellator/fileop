using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
        string canonicalDestinationDirectoryPath);
}

/// <summary>
/// Uses handle-bound Windows volume-GUID names as the stronger relationship proof for
/// Move roots whose 32-bit volume serials are equal. Drive letters and mount-point text
/// are not treated as volume identity.
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
        string canonicalDestinationDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDestinationDirectoryPath);

        try
        {
            var source = ResolveVolumeGuidName(canonicalSourceDirectoryPath);
            var destination = ResolveVolumeGuidName(canonicalDestinationDirectoryPath);
            var same = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
            return new FileOperationVolumeRelationship(
                same
                    ? FileOperationVolumeRelationshipState.SameVolume
                    : FileOperationVolumeRelationshipState.DifferentVolume,
                source,
                destination,
                same
                    ? "Canonical Move roots resolve to the same handle-bound Windows volume GUID."
                    : "Canonical Move roots have equal serial evidence but resolve to different handle-bound Windows volume GUIDs.");
        }
        catch (Exception exception) when (
            exception is IOException or Win32Exception or NotSupportedException)
        {
            return new FileOperationVolumeRelationship(
                FileOperationVolumeRelationshipState.Unavailable,
                SourceVolumeGuidName: null,
                DestinationVolumeGuidName: null,
                "Windows could not obtain a stronger handle-bound volume GUID relationship for the canonical Move roots: " +
                exception.Message);
        }
    }

    private static string ResolveVolumeGuidName(string canonicalDirectoryPath)
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
                $"Opening canonical Move root '{canonicalDirectoryPath}' for volume identity");
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
                    $"Resolving volume GUID for canonical Move root '{canonicalDirectoryPath}'");
            }
            if (length < buffer.Capacity)
            {
                return ExtractVolumeGuidName(buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }

        throw new IOException(
            $"Resolving volume GUID for canonical Move root '{canonicalDirectoryPath}' exceeded the supported path buffer growth limit.");
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
}
