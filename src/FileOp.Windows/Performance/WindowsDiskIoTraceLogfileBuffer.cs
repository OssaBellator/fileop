using System.Runtime.InteropServices;

namespace FileOp.Windows.Performance;

internal sealed class WindowsDiskIoTraceLogfileBuffer : IDisposable
{
    internal const int EventTraceHeaderSize = 48;
    internal const int EventTraceSize = 88;
    internal const int TimeZoneInformationSize = 172;
    internal const int TraceLogfileHeaderSize32 = 272;
    internal const int TraceLogfileHeaderSize64 = 280;
    internal const int EventTraceLogfileSize32 = 416;
    internal const int EventTraceLogfileSize64 = 448;

    private IntPtr _memory;

    private WindowsDiskIoTraceLogfileBuffer()
    {
        TotalSize = IntPtr.Size == 8
            ? EventTraceLogfileSize64
            : EventTraceLogfileSize32;
        _memory = Marshal.AllocHGlobal(TotalSize);
        Marshal.Copy(new byte[TotalSize], 0, _memory, TotalSize);
    }

    public int TotalSize { get; }

    public IntPtr Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_memory == IntPtr.Zero, this);
            return _memory;
        }
    }

    public static int LoggerNameOffset => IntPtr.Size == 8 ? 8 : 4;

    public static int ProcessTraceModeOffset => IntPtr.Size == 8 ? 28 : 20;

    public static int TraceLogfileHeaderOffset => IntPtr.Size == 8 ? 120 : 112;

    public static int TraceLogfilePerfFreqOffset =>
        TraceLogfileHeaderOffset + (IntPtr.Size == 8 ? 256 : 248);

    public static int TraceLogfileBuffersLostOffset =>
        TraceLogfileHeaderOffset + (IntPtr.Size == 8 ? 276 : 268);

    public static int BufferCallbackOffset => IntPtr.Size == 8 ? 400 : 384;

    public static int EventRecordCallbackOffset => IntPtr.Size == 8 ? 424 : 400;

    public static int ContextOffset => IntPtr.Size == 8 ? 440 : 408;

    public static WindowsDiskIoTraceLogfileBuffer CreateForRealtimeOpen(
        IntPtr loggerName,
        uint processTraceMode,
        IntPtr bufferCallback,
        IntPtr eventRecordCallback,
        IntPtr context)
    {
        if (loggerName == IntPtr.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(loggerName));
        }
        if (bufferCallback == IntPtr.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferCallback));
        }
        if (eventRecordCallback == IntPtr.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(eventRecordCallback));
        }

        var buffer = new WindowsDiskIoTraceLogfileBuffer();
        try
        {
            Marshal.WriteIntPtr(buffer.Pointer, LoggerNameOffset, loggerName);
            Marshal.WriteInt32(
                buffer.Pointer,
                ProcessTraceModeOffset,
                unchecked((int)processTraceMode));
            Marshal.WriteIntPtr(buffer.Pointer, BufferCallbackOffset, bufferCallback);
            Marshal.WriteIntPtr(buffer.Pointer, EventRecordCallbackOffset, eventRecordCallback);
            Marshal.WriteIntPtr(buffer.Pointer, ContextOffset, context);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    internal IntPtr ReadPointer(int offset) =>
        Marshal.ReadIntPtr(Pointer, offset);

    internal uint ReadUInt32(int offset) =>
        unchecked((uint)Marshal.ReadInt32(Pointer, offset));

    internal byte[] SnapshotBytes()
    {
        var bytes = new byte[TotalSize];
        Marshal.Copy(Pointer, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        var memory = Interlocked.Exchange(ref _memory, IntPtr.Zero);
        if (memory != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(memory);
        }
    }
}
