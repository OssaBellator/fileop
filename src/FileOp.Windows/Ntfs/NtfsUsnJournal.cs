using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Ntfs;

public sealed class NtfsUsnJournal
{
    private const uint FsctlEnumUsnData = 0x000900B3;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const int ErrorHandleEof = 38;
    private const int ErrorJournalDeleteInProgress = 1178;
    private const int ErrorJournalNotActive = 1179;
    private const int ErrorJournalEntryDeleted = 1181;
    private const int BufferSize = 1024 * 1024;
    private const int UsnRecordV2MinimumLength = 60;

    public NtfsJournalState Query(NtfsVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        using var handle = OpenVolume(volume);
        return Query(handle, volume);
    }

    public IEnumerable<NtfsMftEntry> EnumerateMft(
        NtfsVolume volume,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = GetCurrentCheckpoint(volume);
        return EnumerateMft(volume, checkpoint, cancellationToken);
    }

    public IEnumerable<NtfsMftEntry> EnumerateMft(
        NtfsVolume volume,
        NtfsJournalCheckpoint snapshotCheckpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        return EnumerateMftIterator(volume, snapshotCheckpoint, cancellationToken);
    }

    private IEnumerable<NtfsMftEntry> EnumerateMftIterator(
        NtfsVolume volume,
        NtfsJournalCheckpoint snapshotCheckpoint,
        CancellationToken cancellationToken)
    {
        using var handle = OpenVolume(volume);
        var journal = Query(handle, volume);
        ValidateCheckpoint(volume, snapshotCheckpoint, journal);

        var input = new byte[24];
        var output = ArrayPool<byte>.Shared.Rent(BufferSize);
        ulong startFileReferenceNumber = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(0, 8), startFileReferenceNumber);
                BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8, 8), 0);
                BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16, 8), snapshotCheckpoint.NextUsn);

                if (!DeviceIoControl(
                    handle,
                    FsctlEnumUsnData,
                    input,
                    (uint)input.Length,
                    output,
                    (uint)BufferSize,
                    out var bytesReturned,
                    IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorHandleEof)
                    {
                        yield break;
                    }

                    if (IsJournalResetError(error))
                    {
                        throw CreateJournalResetException(volume, "enumerating MFT records");
                    }

                    throw CreateWin32Exception(error, volume, "enumerate MFT records");
                }

                if (bytesReturned < sizeof(ulong))
                {
                    yield break;
                }

                var nextFileReferenceNumber = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, sizeof(ulong)));
                if (bytesReturned > sizeof(ulong))
                {
                    var records = ParseV2Records(
                        output.AsSpan(sizeof(ulong), checked((int)bytesReturned) - sizeof(ulong)));
                    foreach (var record in records)
                    {
                        yield return record;
                    }
                }

                if (nextFileReferenceNumber <= startFileReferenceNumber)
                {
                    yield break;
                }

                startFileReferenceNumber = nextFileReferenceNumber;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    public NtfsJournalBatch ReadChanges(
        NtfsVolume volume,
        NtfsJournalCheckpoint checkpoint,
        uint reasonMask = uint.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        using var handle = OpenVolume(volume);
        var state = Query(handle, volume);
        ValidateCheckpoint(volume, checkpoint, state);
        cancellationToken.ThrowIfCancellationRequested();

        var input = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(0, 8), checkpoint.NextUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8, 4), reasonMask);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12, 4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32, 8), state.JournalId);

        var output = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            if (!DeviceIoControl(
                handle,
                FsctlReadUsnJournal,
                input,
                (uint)input.Length,
                output,
                (uint)BufferSize,
                out var bytesReturned,
                IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (IsJournalResetError(error))
                {
                    throw CreateJournalResetException(volume, "reading changes");
                }

                throw CreateWin32Exception(error, volume, "read the USN journal");
            }

            if (bytesReturned < sizeof(long))
            {
                throw new InvalidDataException("The USN journal returned an incomplete cursor header.");
            }

            var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(0, sizeof(long)));
            IReadOnlyList<NtfsMftEntry> records = bytesReturned == sizeof(long)
                ? []
                : ParseV2Records(output.AsSpan(sizeof(long), checked((int)bytesReturned) - sizeof(long)));

            return new NtfsJournalBatch(
                checkpoint,
                new NtfsJournalCheckpoint(state.JournalId, nextUsn),
                records);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    public NtfsJournalCheckpoint GetCurrentCheckpoint(NtfsVolume volume)
    {
        var state = Query(volume);
        return new NtfsJournalCheckpoint(state.JournalId, state.NextUsn);
    }

    private static void ValidateCheckpoint(
        NtfsVolume volume,
        NtfsJournalCheckpoint checkpoint,
        NtfsJournalState state)
    {
        if (checkpoint.JournalId != state.JournalId)
        {
            throw new NtfsJournalResetException(
                $"The USN journal for {volume.RootPath} was replaced. A fresh namespace snapshot is required.");
        }

        var minimumReadableUsn = Math.Max(state.FirstUsn, state.LowestValidUsn);
        if (checkpoint.NextUsn < minimumReadableUsn)
        {
            throw new NtfsJournalResetException(
                $"The saved USN {checkpoint.NextUsn} is older than the journal's minimum readable USN {minimumReadableUsn}. A fresh namespace snapshot is required.");
        }

        if (checkpoint.NextUsn > state.NextUsn)
        {
            throw new NtfsJournalResetException(
                $"The saved USN {checkpoint.NextUsn} is ahead of the journal's current USN {state.NextUsn}. A fresh namespace snapshot is required.");
        }
    }

    private static NtfsJournalState Query(SafeFileHandle handle, NtfsVolume volume)
    {
        var output = new byte[128];
        if (!DeviceIoControl(
            handle,
            FsctlQueryUsnJournal,
            null,
            0,
            output,
            (uint)output.Length,
            out var bytesReturned,
            IntPtr.Zero))
        {
            throw CreateWin32Exception(Marshal.GetLastWin32Error(), volume, "query the USN journal");
        }

        if (bytesReturned < 56)
        {
            throw new InvalidDataException($"The USN journal query for {volume.RootPath} returned only {bytesReturned} bytes.");
        }

        return new NtfsJournalState(
            BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(32, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(40, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(48, 8)));
    }

    internal static IReadOnlyList<NtfsMftEntry> ParseV2Records(ReadOnlySpan<byte> data)
    {
        var records = new List<NtfsMftEntry>();
        var offset = 0;

        while (offset < data.Length)
        {
            if (data.Length - offset < 8)
            {
                throw new InvalidDataException("The USN buffer ended in the middle of a record header.");
            }

            var record = data[offset..];
            var recordLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record[..4]));
            var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));

            if (recordLength < UsnRecordV2MinimumLength || recordLength > record.Length)
            {
                throw new InvalidDataException($"Invalid USN record length {recordLength}.");
            }

            if (majorVersion != 2)
            {
                throw new NotSupportedException($"USN record major version {majorVersion} is not supported by the NTFS provider.");
            }

            var recordSlice = record[..recordLength];
            var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(recordSlice.Slice(56, 2));
            var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(recordSlice.Slice(58, 2));

            if ((fileNameLength & 1) != 0 ||
                fileNameOffset < UsnRecordV2MinimumLength ||
                fileNameOffset + fileNameLength > recordLength)
            {
                throw new InvalidDataException("The USN record contains an invalid file-name range.");
            }

            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(recordSlice.Slice(32, 8));
            DateTimeOffset timestamp;
            try
            {
                timestamp = DateTimeOffset.FromFileTime(fileTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                timestamp = DateTimeOffset.MinValue;
            }

            records.Add(new NtfsMftEntry(
                BinaryPrimitives.ReadUInt64LittleEndian(recordSlice.Slice(8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(recordSlice.Slice(16, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(recordSlice.Slice(24, 8)),
                timestamp,
                (UsnReason)BinaryPrimitives.ReadUInt32LittleEndian(recordSlice.Slice(40, 4)),
                (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(recordSlice.Slice(52, 4)),
                Encoding.Unicode.GetString(recordSlice.Slice(fileNameOffset, fileNameLength))));

            offset += recordLength;
        }

        return records;
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
            throw CreateWin32Exception(error, volume, "open the volume");
        }

        return handle;
    }

    private static bool IsJournalResetError(int error) =>
        error is ErrorJournalDeleteInProgress or ErrorJournalNotActive or ErrorJournalEntryDeleted;

    private static NtfsJournalResetException CreateJournalResetException(NtfsVolume volume, string operation) =>
        new($"The USN journal for {volume.RootPath} changed while {operation}. A fresh namespace snapshot is required.");

    private static Win32Exception CreateWin32Exception(int error, NtfsVolume volume, string operation) =>
        new(error, $"Could not {operation} on {volume.RootPath}: {new Win32Exception(error).Message}");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
