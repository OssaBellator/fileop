using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Ntfs;

public sealed class NtfsFileMetadataReader
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNotFound = 1168;
    private const int FileBasicInfo = 0;
    private const int FileStandardInfo = 1;
    private const int FileBasicInfoSize = 40;
    private const int FileStandardInfoSize = 24;

    public NtfsFileMetadata? TryRead(NtfsVolume volume, ulong fileReferenceNumber)
    {
        ArgumentNullException.ThrowIfNull(volume);

        using var volumeHandle = OpenVolume(volume);
        return TryRead(volume, volumeHandle, fileReferenceNumber);
    }

    public IReadOnlyDictionary<ulong, NtfsFileMetadata> ReadBatch(
        NtfsVolume volume,
        IEnumerable<ulong> fileReferenceNumbers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(fileReferenceNumbers);

        using var volumeHandle = OpenVolume(volume);
        var result = new Dictionary<ulong, NtfsFileMetadata>();

        foreach (var fileReferenceNumber in fileReferenceNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = TryRead(volume, volumeHandle, fileReferenceNumber);
            if (metadata is not null)
            {
                result[fileReferenceNumber] = metadata;
            }
        }

        return result;
    }

    private static NtfsFileMetadata? TryRead(
        NtfsVolume volume,
        SafeFileHandle volumeHandle,
        ulong fileReferenceNumber)
    {
        var descriptor = FileIdDescriptor.Create(fileReferenceNumber);
        using var fileHandle = OpenFileById(
            volumeHandle,
            ref descriptor,
            FileReadAttributes,
            ShareReadWriteDelete,
            IntPtr.Zero,
            FileFlagBackupSemantics);

        if (fileHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound or ErrorNotFound)
            {
                return null;
            }

            throw CreateWin32Exception(error, volume, fileReferenceNumber, "open the file by ID");
        }

        var standardInfo = new byte[FileStandardInfoSize];
        if (!GetFileInformationByHandleEx(
            fileHandle,
            FileStandardInfo,
            standardInfo,
            (uint)standardInfo.Length))
        {
            throw CreateWin32Exception(
                Marshal.GetLastWin32Error(),
                volume,
                fileReferenceNumber,
                "read standard file metadata");
        }

        // FILE_STANDARD_INFO exposes DeletePending immediately before Directory.
        // A delete-pending file can remain openable by ID while its final namespace entry is
        // already queued for removal; treating it as absent prevents a snapshot/catch-up race
        // from resurrecting a row that Windows is deleting.
        if (standardInfo[20] != 0)
        {
            return null;
        }

        var basicInfo = new byte[FileBasicInfoSize];
        if (!GetFileInformationByHandleEx(
            fileHandle,
            FileBasicInfo,
            basicInfo,
            (uint)basicInfo.Length))
        {
            throw CreateWin32Exception(
                Marshal.GetLastWin32Error(),
                volume,
                fileReferenceNumber,
                "read basic file metadata");
        }

        var allocationSize = BinaryPrimitives.ReadInt64LittleEndian(standardInfo.AsSpan(0, 8));
        var endOfFile = BinaryPrimitives.ReadInt64LittleEndian(standardInfo.AsSpan(8, 8));
        var numberOfLinks = BinaryPrimitives.ReadUInt32LittleEndian(standardInfo.AsSpan(16, 4));
        var isDirectory = standardInfo[21] != 0;
        var lastWriteFileTime = BinaryPrimitives.ReadInt64LittleEndian(basicInfo.AsSpan(16, 8));
        var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(basicInfo.AsSpan(32, 4));

        return new NtfsFileMetadata(
            Math.Max(0, endOfFile),
            Math.Max(0, allocationSize),
            numberOfLinks,
            isDirectory,
            FromFileTime(lastWriteFileTime),
            attributes);
    }

    private static DateTimeOffset FromFileTime(long fileTime)
    {
        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static SafeFileHandle OpenVolume(NtfsVolume volume)
    {
        var handle = CreateFileW(
            volume.DevicePath,
            GenericRead,
            ShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open {volume.RootPath} for file-ID metadata reads: {new Win32Exception(error).Message}");
        }

        return handle;
    }

    private static Win32Exception CreateWin32Exception(
        int error,
        NtfsVolume volume,
        ulong fileReferenceNumber,
        string operation) =>
        new(
            error,
            $"Could not {operation} for file reference {fileReferenceNumber} on {volume.RootPath}: {new Win32Exception(error).Message}");

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)]
        public uint Size;

        [FieldOffset(4)]
        public FileIdType Type;

        [FieldOffset(8)]
        public long FileId;

        public static FileIdDescriptor Create(ulong fileReferenceNumber) => new()
        {
            Size = 24,
            Type = FileIdType.FileId,
            FileId = unchecked((long)fileReferenceNumber),
        };
    }

    private enum FileIdType : uint
    {
        FileId = 0,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle hVolumeHint,
        ref FileIdDescriptor lpFileId,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        [Out] byte[] lpFileInformation,
        uint dwBufferSize);
}
