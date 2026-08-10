using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace FileOp.Windows.Performance;

internal interface IWindowsDiskIoNativeTraceCallbackSink
{
    bool OnEventRecord(IntPtr eventRecord);

    bool OnBuffer(IntPtr logfile);
}

internal sealed class WindowsDiskIoNativeTraceConsumerApi :
    IWindowsDiskIoTraceConsumerApi,
    IWindowsDiskIoTraceEvidenceSource
{
    private const string NativeLibrary = "sechost.dll";

    private readonly object _gate = new();
    private readonly IWindowsDiskIoNativeTraceCallbackSink _sink;
    private readonly EventRecordCallbackNative _eventRecordCallback;
    private readonly BufferCallbackNative _bufferCallback;

    private ulong _openedHandle;
    private bool _openActive;
    private bool _processStarted;
    private bool _processActive;
    private bool _callbackCancellationRequested;
    private ExceptionDispatchInfo? _callbackFault;
    private WindowsDiskIoTraceLogfileBuffer? _logfileBuffer;
    private IntPtr _loggerNameMemory;

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
            if (_openedHandle != 0 || _openActive || _processActive || _logfileBuffer is not null)
            {
                throw new InvalidOperationException(
                    "This native ETW consumer adapter is already opening, owns, or is draining a processing handle.");
            }

            _openActive = true;
            _processStarted = false;
            _callbackCancellationRequested = false;
            _callbackFault = null;
        }

        var loggerName = IntPtr.Zero;
        WindowsDiskIoTraceLogfileBuffer? logfile = null;
        try
        {
            loggerName = Marshal.StringToHGlobalUni(sessionName);
            logfile = WindowsDiskIoTraceLogfileBuffer.CreateForRealtimeOpen(
                loggerName,
                processTraceMode,
                Marshal.GetFunctionPointerForDelegate(_bufferCallback),
                Marshal.GetFunctionPointerForDelegate(_eventRecordCallback),
                context: IntPtr.Zero);

            var handle = OpenTraceW(logfile.Pointer);
            if (WindowsDiskIoTraceConsumerPolicy.IsInvalidProcessingHandle(handle))
            {
                return new WindowsDiskIoNativeOpenResult(
                    handle,
                    unchecked((uint)Marshal.GetLastPInvokeError()));
            }

            lock (_gate)
            {
                _openedHandle = handle;
                _loggerNameMemory = loggerName;
                _logfileBuffer = logfile;
                loggerName = IntPtr.Zero;
                logfile = null;
            }

            return new WindowsDiskIoNativeOpenResult(
                handle,
                WindowsDiskIoTraceConsumerPolicy.ErrorSuccess);
        }
        finally
        {
            logfile?.Dispose();
            if (loggerName != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(loggerName);
            }

            lock (_gate)
            {
                _openActive = false;
            }
        }
    }

    public WindowsDiskIoTraceEvidence ReadTraceEvidence(ulong processingHandle)
    {
        lock (_gate)
        {
            EnsureOwnedHandle(processingHandle);
            if (_openActive)
            {
                throw new InvalidOperationException(
                    "ETW trace evidence cannot be read while OpenTraceW is still completing.");
            }
            if (_processActive)
            {
                throw new InvalidOperationException(
                    "ETW trace evidence cannot be read while ProcessTrace is actively mutating the retained logfile state.");
            }

            return WindowsDiskIoTraceEvidenceReader.Read(_logfileBuffer!);
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
            _processActive = true;
        }

        try
        {
            var handle = processingHandle;
            var status = ProcessTraceNative(
                ref handle,
                handleCount: 1,
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
        finally
        {
            lock (_gate)
            {
                _processActive = false;
                ReleaseNativeOpenStateIfSafe();
            }
        }
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

                ReleaseNativeOpenStateIfSafe();
            }
        }

        return status;
    }

    private void ReleaseNativeOpenStateIfSafe()
    {
        if (_openedHandle != 0 || _openActive || _processActive)
        {
            return;
        }

        _logfileBuffer?.Dispose();
        _logfileBuffer = null;

        if (_loggerNameMemory != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_loggerNameMemory);
            _loggerNameMemory = IntPtr.Zero;
        }
    }

    private void EnsureOwnedHandle(ulong processingHandle)
    {
        if (processingHandle == 0 ||
            WindowsDiskIoTraceConsumerPolicy.IsInvalidProcessingHandle(processingHandle) ||
            _openedHandle != processingHandle ||
            _logfileBuffer is null ||
            _loggerNameMemory == IntPtr.Zero)
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
        if (logfile == IntPtr.Zero)
        {
            CaptureCallbackFault(new InvalidDataException(
                "ETW invoked FileOp's BufferCallback with a null EVENT_TRACE_LOGFILEW pointer."));
            return 0;
        }

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
    private static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport(
        NativeLibrary,
        EntryPoint = "ProcessTrace",
        ExactSpelling = true)]
    private static extern uint ProcessTraceNative(
        ref ulong handleArray,
        uint handleCount,
        IntPtr startTime,
        IntPtr endTime);

    [DllImport(
        NativeLibrary,
        EntryPoint = "CloseTrace",
        ExactSpelling = true)]
    private static extern uint CloseTraceNative(ulong traceHandle);
}
