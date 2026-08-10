using System.Runtime.InteropServices;
using System.Text;

namespace FileOp.Windows.Performance;

internal sealed class WindowsDiskIoTracePropertiesBuffer : IDisposable
{
    internal const uint WnodeFlagTracedGuid = 0x00020000;
    internal const uint RequestedBufferSizeKilobytes = 16;
    internal const uint RequestedMinimumBuffers = 0;
    internal const uint RequestedMaximumBuffers = 64;

    private IntPtr _memory;

    private WindowsDiskIoTracePropertiesBuffer(bool forStart)
    {
        StructureSize = Marshal.SizeOf<EventTracePropertiesNative>();
        var loggerNameBytes = Encoding.Unicode.GetBytes(
            WindowsDiskIoSystemSessionPolicy.SessionName + '\0');
        TotalSize = checked(StructureSize + loggerNameBytes.Length);
        _memory = Marshal.AllocHGlobal(TotalSize);
        Marshal.Copy(new byte[TotalSize], 0, _memory, TotalSize);

        var properties = new EventTracePropertiesNative
        {
            Wnode = new WnodeHeaderNative
            {
                BufferSize = checked((uint)TotalSize),
                Guid = WindowsDiskIoSystemSessionPolicy.SessionGuid,
                ClientContext = forStart
                    ? WindowsDiskIoSystemSessionPolicy.QueryPerformanceCounterClock
                    : 0,
                Flags = WnodeFlagTracedGuid,
            },
            LogFileNameOffset = 0,
            LoggerNameOffset = checked((uint)StructureSize),
        };

        if (forStart)
        {
            properties.BufferSize = RequestedBufferSizeKilobytes;
            properties.MinimumBuffers = RequestedMinimumBuffers;
            properties.MaximumBuffers = RequestedMaximumBuffers;
            properties.LogFileMode = WindowsDiskIoSystemSessionPolicy.LogFileMode;
            properties.EnableFlags = WindowsDiskIoSystemSessionPolicy.EnableFlags;
        }

        Marshal.StructureToPtr(properties, _memory, fDeleteOld: false);
        Marshal.Copy(
            loggerNameBytes,
            0,
            IntPtr.Add(_memory, StructureSize),
            loggerNameBytes.Length);
    }

    public int StructureSize { get; }

    public int TotalSize { get; }

    public IntPtr Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_memory == IntPtr.Zero, this);
            return _memory;
        }
    }

    internal EventTracePropertiesNative Snapshot =>
        Marshal.PtrToStructure<EventTracePropertiesNative>(Pointer);

    internal string? ReadLoggerName() =>
        Marshal.PtrToStringUni(IntPtr.Add(Pointer, StructureSize));

    public static WindowsDiskIoTracePropertiesBuffer CreateForStart() => new(forStart: true);

    public static WindowsDiskIoTracePropertiesBuffer CreateForStop() => new(forStart: false);

    public void Dispose()
    {
        var memory = Interlocked.Exchange(ref _memory, IntPtr.Zero);
        if (memory != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WnodeHeaderNative
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTracePropertiesNative
    {
        public WnodeHeaderNative Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }
}
