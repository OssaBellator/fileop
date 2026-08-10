using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace FileOp.Windows.Performance;

internal interface IWindowsDiskIoNativeTraceCallbackSink
{
    bool OnEventRecord(IntPtr eventRecord);

    bool OnBuffer(IntPtr logfile);
}

internal sealed class WindowsDiskIoNativeTraceConsumerApi : IWindowsDiskIoTraceConsumerApi
{
    private const string NativeLibrary = "sechost.dll";

    private readonly object _gate = new();
    private readonly IWindowsDiskIoNativeTraceCallbackSink _sink;
    private readonly EventRecordCallbackNative _eventRecordCallback;
    private readonly BufferCallbackNative _bufferCallback;

    private ulong _openedHandle;
    private bool _processStarted;
    private bool _callbackCancellationRequested;
    private ExceptionDispatchInfo? _callbackFault;

    public WindowsDiskIoNativeTraceConsumerApi(IWindowsDiskIoNativeTraceCallbackSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _eventRecordCallback = EventRecordCallback;
        _bufferCallback = BufferCallback;
    }

    public WindowsDiskIoNativeOpenResult OpenRealtime(
        string sessionName,
        uint processTraceMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        lock (_gate)
        {
            if (_openedHandle != 0)
            {
                throw new InvalidOperationException(
                    "This native ETW consumer adapter already owns an open processing handle.");
            }

            _processStarted = false;
            _callbackCancellationRequested = false;
            _callbackFault = null;
        }

        var loggerName = Marshal.StringToHGlobalUni(sessionName);
        try
        {
            var logfile = new EventTraceLogfileWNative
            {
                LogFileName = IntPtr.Zero,
                LoggerName = loggerName,
                ProcessTraceMode = processTraceMode,
                BufferCallback = Marshal.GetFunctionPointerForDelegate(_bufferCallback),
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_eventRecordCallback),
                Context = IntPtr.Zero,
            };

            var handle = OpenTraceW(ref logfile);
            if (WindowsDiskIoTraceConsumerPolicy.IsInvalidProcessingHandle(handle))
            {
                return new WindowsDiskIoNativeOpenResult(
                    handle,
                    unchecked((uint)Marshal.GetLastPInvokeError()));
            }

            lock (_gate)
            {
                _openedHandle = handle;
            }

            return new WindowsDiskIoNativeOpenResult(
                handle,
                WindowsDiskIoTraceConsumerPolicy.ErrorSuccess);
        }
        finally
        {
            Marshal.FreeHGlobal(loggerName);
        }
    }

    public uint ProcessTrace(ulong processingHandle)
    {
        lock (_gate)
        {
            EnsureOwnedHandle(processingHandle);
            if (_processStarted)
            {
                throw new InvalidOperationException(
                    "Native ProcessTrace is single-shot for each FileOp processing handle.");
            }

            _processStarted = true;
        }

        var handles = new[] { processingHandle };
        var status = ProcessTraceNative(
            handles,
            checked((uint)handles.Length),
            IntPtr.Zero,
            IntPtr.Zero);

        ExceptionDispatchInfo? callbackFault;
        lock (_gate)
        {
            callbackFault = _callbackFault;
        }

        callbackFault?.Throw();
        return status;
    }

    public uint CloseTrace(ulong processingHandle)
    {
        lock (_gate)
        {
            EnsureOwnedHandle(processingHandle);
        }

        var status = CloseTraceNative(processingHandle);
        if (WindowsDiskIoTraceConsumerPolicy.TryClassifyExpectedCloseResult(
            status,
            out _))
        {
            lock (_gate)
            {
                if (_openedHandle == processingHandle)
                {
                    _openedHandle = 0;
                }
            }
        }

        return status;
    }

    private void EnsureOwnedHandle(ulong processingHandle)
    {
        if (processingHandle == 0 ||
            WindowsDiskIoTraceConsumerPolicy.IsInvalidProcessingHandle(processingHandle) ||
            _openedHandle != processingHandle)
        {
            throw new InvalidOperationException(
                "The supplied ETW processing handle is not owned by this FileOp consumer adapter.");
        }
    }

    private void EventRecordCallback(IntPtr eventRecord)
    {
        if (eventRecord == IntPtr.Zero)
        {
            CaptureCallbackFault(new InvalidDataException(
                "ETW invoked FileOp's EventRecordCallback with a null EVENT_RECORD pointer."));
            return;
        }

        lock (_gate)
        {
            if (_callbackCancellationRequested || _callbackFault is not null)
            {
                return;
            }
        }

        try
        {
            if (!_sink.OnEventRecord(eventRecord))
            {
                lock (_gate)
                {
                    _callbackCancellationRequested = true;
                }
            }
        }
        catch (Exception exception)
        {
            CaptureCallbackFault(exception);
        }
    }

    private uint BufferCallback(IntPtr logfile)
    {
        lock (_gate)
        {
            if (_callbackCancellationRequested || _callbackFault is not null)
            {
                return 0;
            }
        }

        try
        {
            if (!_sink.OnBuffer(logfile))
            {
                lock (_gate)
                {
                    _callbackCancellationRequested = true;
                }

                return 0;
            }

            return 1;
        }
        catch (Exception exception)
        {
            CaptureCallbackFault(exception);
            return 0;
        }
    }

    private void CaptureCallbackFault(Exception exception)
    {
        lock (_gate)
        {
            _callbackFault ??= ExceptionDispatchInfo.Capture(exception);
            _callbackCancellationRequested = true;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EventRecordCallbackNative(IntPtr eventRecord);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate uint BufferCallbackNative(IntPtr logfile);

    [DllImport(
        NativeLibrary,
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfileWNative logfile);

    [DllImport(
        NativeLibrary,
        EntryPoint = "ProcessTrace",
        ExactSpelling = true)]
    private static extern uint ProcessTraceNative(
        [In] ulong[] handleArray,
        uint handleCount,
        IntPtr startTime,
        IntPtr endTime);

    [DllImport(
        NativeLibrary,
        EntryPoint = "CloseTrace",
        ExactSpelling = true)]
    private static extern uint CloseTraceNative(ulong traceHandle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceLogfileWNative
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTraceNative CurrentEvent;
        public TraceLogfileHeaderNative LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceNative
    {
        public EventTraceHeaderNative Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceHeaderNative
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid EventGuid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TraceLogfileHeaderNative
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TimeZoneInformationNative TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TimeZoneInformationNative
    {
        public int Bias;
        public Utf16Name32Native StandardName;
        public SystemTimeNative StandardDate;
        public int StandardBias;
        public Utf16Name32Native DaylightName;
        public SystemTimeNative DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct Utf16Name32Native
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemTimeNative
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }
}
