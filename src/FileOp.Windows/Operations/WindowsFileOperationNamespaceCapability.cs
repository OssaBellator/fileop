using System;
using System.IO;
using System.Runtime.InteropServices;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

public enum FileOperationNamespaceCapabilityState
{
    SupportedCaseInsensitive,
    UnsupportedCaseSensitiveDirectory,
    Unavailable,
}

public sealed record FileOperationNamespaceCapability(
    string CanonicalDirectoryPath,
    FileOperationNamespaceCapabilityState State,
    string Summary)
{
    public bool CanUseCurrentMutationModel =>
        State == FileOperationNamespaceCapabilityState.SupportedCaseInsensitive;
}

/// <summary>
/// Reports namespace evidence for one already canonical directory. The caller
/// owns policy about which operation roots must be supported.
/// </summary>
public interface IFileOperationNamespaceCapabilityProbe
{
    FileOperationNamespaceCapability QueryDirectory(string canonicalDirectoryPath);
}

/// <summary>
/// Queries the per-directory NTFS case-sensitive flag through NtQueryInformationFile.
/// The current FileOp operation/index path model is intentionally case-insensitive,
/// so a flagged directory fails closed for mutation rather than aliasing exact-case names.
/// </summary>
public sealed class WindowsFileOperationNamespaceCapabilityProbe : IFileOperationNamespaceCapabilityProbe
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileCaseSensitiveInformation = 71;
    private const uint FileCsFlagCaseSensitiveDir = 0x00000001;

    public FileOperationNamespaceCapability QueryDirectory(string canonicalDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDirectoryPath);
        var fullPath = Path.GetFullPath(canonicalDirectoryPath);
        using var handle = CreateFileW(
            fullPath,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return new FileOperationNamespaceCapability(
                fullPath,
                FileOperationNamespaceCapabilityState.Unavailable,
                $"The directory could not be opened for case-sensitivity inspection (Win32 {Marshal.GetLastWin32Error()}).");
        }

        var status = NtQueryInformationFile(
            handle,
            out _,
            out var information,
            checked((uint)Marshal.SizeOf<FileCaseSensitiveInformationData>()),
            FileCaseSensitiveInformation);
        if (status < 0)
        {
            return new FileOperationNamespaceCapability(
                fullPath,
                FileOperationNamespaceCapabilityState.Unavailable,
                $"Windows could not query FileCaseSensitiveInformation (NTSTATUS 0x{unchecked((uint)status):X8}).");
        }

        if ((information.Flags & FileCsFlagCaseSensitiveDir) != 0)
        {
            return new FileOperationNamespaceCapability(
                fullPath,
                FileOperationNamespaceCapabilityState.UnsupportedCaseSensitiveDirectory,
                "This directory uses per-directory case-sensitive NTFS semantics. FileOp mutation remains disabled until exact-case namespace identity is represented end-to-end.");
        }

        return new FileOperationNamespaceCapability(
            fullPath,
            FileOperationNamespaceCapabilityState.SupportedCaseInsensitive,
            "The directory uses the case-insensitive namespace semantics supported by the current FileOp operation model.");
    }

    /// <summary>
    /// Convenience guard for non-injectable callers. Move execution uses the
    /// injectable validator policy so both roots are deterministically testable.
    /// </summary>
    public void RequireSupportedMutationRoots(FileOperationExecutionValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        foreach (var path in new[]
        {
            validation.SourceDirectory.CanonicalPath,
            validation.DestinationDirectory.CanonicalPath,
        })
        {
            var capability = QueryDirectory(path);
            if (!capability.CanUseCurrentMutationModel)
            {
                throw new NotSupportedException(capability.Summary);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        out FileCaseSensitiveInformationData fileInformation,
        uint length,
        int fileInformationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformationData
    {
        public uint Flags;
    }
}
