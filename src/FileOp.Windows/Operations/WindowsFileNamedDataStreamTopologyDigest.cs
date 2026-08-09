using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

internal static class WindowsFileNamedDataStreamTopologyDigest
{
    private const int FileStreamInfo = 7;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int ErrorHandleEof = 38;
    private const int HeaderBytes = 24;
    private const int InitialBufferBytes = 4096;
    private const int MaximumBufferBytes = 1024 * 1024;
    private const int MaximumNamedStreams = 4096;
    private static readonly byte[] CanonicalPrefix = Encoding.ASCII.GetBytes("FileOp.NamedDataStreams.v1\0");
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static FileNamedDataStreamTopologyEvidence Read(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid)
        {
            throw new IOException("Cannot query named data-stream topology from an invalid file handle.");
        }

        var bufferSize = InitialBufferBytes;
        while (bufferSize <= MaximumBufferBytes)
        {
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetFileInformationByHandleEx(
                    handle,
                    FileStreamInfo,
                    buffer,
                    checked((uint)bufferSize)))
                {
                    var entries = Parse(buffer, bufferSize);
                    return CreateEvidence(entries);
                }

                var error = Marshal.GetLastWin32Error();
                if (error == ErrorHandleEof)
                {
                    throw new IOException(
                        "FileStreamInfo returned no stream inventory; named-data-stream topology is unavailable for this file-system/handle state.");
                }

                if (error is not ErrorInsufficientBuffer and not ErrorMoreData)
                {
                    throw new IOException(
                        $"Enumerating named data streams failed with Win32 error {error}: {new Win32Exception(error).Message}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            bufferSize = checked(bufferSize * 2);
        }

        throw new InvalidDataException(
            $"Named data-stream inventory exceeded the {MaximumBufferBytes} byte safety limit.");
    }

    private static IReadOnlyList<NamedStream> Parse(IntPtr buffer, int bufferSize)
    {
        var result = new List<NamedStream>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var entriesSeen = 0;
        var sawDefaultDataStream = false;

        while (true)
        {
            entriesSeen++;
            if (entriesSeen > MaximumNamedStreams + 1)
            {
                throw new InvalidDataException("Named data-stream inventory contains implausibly many entries.");
            }

            if (offset < 0 || offset > bufferSize - HeaderBytes)
            {
                throw new InvalidDataException("FILE_STREAM_INFO entry offset is outside the returned buffer.");
            }

            var entry = IntPtr.Add(buffer, offset);
            var nextEntryOffset = unchecked((uint)Marshal.ReadInt32(entry, 0));
            var streamNameLength = unchecked((uint)Marshal.ReadInt32(entry, 4));
            var streamSize = Marshal.ReadInt64(entry, 8);

            if (streamNameLength == 0 ||
                (streamNameLength & 1u) != 0 ||
                streamNameLength > int.MaxValue ||
                streamNameLength > bufferSize - offset - HeaderBytes)
            {
                throw new InvalidDataException("FILE_STREAM_INFO contains an invalid UTF-16 stream-name length.");
            }

            if (streamSize < 0)
            {
                throw new InvalidDataException("FILE_STREAM_INFO contains a negative logical stream size.");
            }

            var name = Marshal.PtrToStringUni(
                IntPtr.Add(entry, HeaderBytes),
                checked((int)streamNameLength / sizeof(char)))
                ?? throw new InvalidDataException("FILE_STREAM_INFO returned a null stream name.");
            if (name.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException("FILE_STREAM_INFO returned an embedded NUL in a stream name.");
            }

            if (string.Equals(name, "::$DATA", StringComparison.OrdinalIgnoreCase))
            {
                if (sawDefaultDataStream)
                {
                    throw new InvalidDataException("FILE_STREAM_INFO returned the default data stream more than once.");
                }

                sawDefaultDataStream = true;
            }
            else
            {
                if (!IsNamedDataStream(name))
                {
                    throw new InvalidDataException(
                        "FILE_STREAM_INFO returned an unexpected non-$DATA stream entry; raw stream names are not included in diagnostics.");
                }

                if (!seen.Add(name))
                {
                    throw new InvalidDataException("FILE_STREAM_INFO returned a duplicate named data stream.");
                }

                result.Add(new NamedStream(name, streamSize));
                if (result.Count > MaximumNamedStreams)
                {
                    throw new InvalidDataException("Named data-stream inventory exceeds the supported stream-count safety limit.");
                }
            }

            if (nextEntryOffset == 0)
            {
                break;
            }

            if ((nextEntryOffset & 7u) != 0 ||
                nextEntryOffset < HeaderBytes + streamNameLength ||
                nextEntryOffset > int.MaxValue ||
                nextEntryOffset > bufferSize - offset)
            {
                throw new InvalidDataException("FILE_STREAM_INFO contains an invalid next-entry offset.");
            }

            offset = checked(offset + (int)nextEntryOffset);
        }

        if (!sawDefaultDataStream)
        {
            throw new InvalidDataException(
                "FILE_STREAM_INFO did not include the unnamed default ::$DATA stream; topology evidence is not trusted.");
        }

        return result
            .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsNamedDataStream(string name)
    {
        if (name.Length <= ":$DATA".Length || name[0] != ':')
        {
            return false;
        }

        if (!name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var streamNameLength = name.Length - 1 - ":$DATA".Length;
        return streamNameLength > 0 &&
            name.AsSpan(1, streamNameLength).IndexOf(':') < 0;
    }

    private static FileNamedDataStreamTopologyEvidence CreateEvidence(
        IReadOnlyList<NamedStream> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(CanonicalPrefix);
        Span<byte> scalar = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(scalar[..4], entries.Count);
        hash.AppendData(scalar[..4]);

        foreach (var entry in entries)
        {
            byte[] nameBytes;
            try
            {
                nameBytes = StrictUtf8.GetBytes(entry.Name);
            }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "FILE_STREAM_INFO returned a stream name containing invalid UTF-16; topology evidence is not trusted.",
                    exception);
            }

            BinaryPrimitives.WriteInt32LittleEndian(scalar[..4], nameBytes.Length);
            hash.AppendData(scalar[..4]);
            hash.AppendData(nameBytes);
            BinaryPrimitives.WriteInt64LittleEndian(scalar, entry.Size);
            hash.AppendData(scalar);
        }

        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new FileNamedDataStreamTopologyEvidence(
            FileNamedDataStreamTopologyEvidence.CurrentFormatVersion,
            entries.Count,
            digest);
    }

    private sealed record NamedStream(string Name, long Size);

    [DllImport(
        "kernel32.dll",
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int FileInformationClass,
        IntPtr lpFileInformation,
        uint dwBufferSize);
}
