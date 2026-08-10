using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FileOp.Windows.Performance;

internal interface IWindowsDiskIoTraceControlApi
{
    uint StartTrace(
        out ulong traceId,
        string instanceName,
        IntPtr properties);

    uint StopTrace(
        ulong traceId,
        IntPtr properties);
}

internal sealed record WindowsDiskIoTraceSessionStartResult(
    WindowsDiskIoOwnedTraceSession? Session,
    WindowsDiskIoSessionFailure? Failure)
{
    public bool Started => Session is not null;

    public static WindowsDiskIoTraceSessionStartResult Success(
        WindowsDiskIoOwnedTraceSession session) =>
        new(session, null);

    public static WindowsDiskIoTraceSessionStartResult Unavailable(
        WindowsDiskIoSessionFailure failure) =>
        new(null, failure);
}

internal sealed class WindowsDiskIoTraceSessionController
{
    private readonly IWindowsDiskIoTraceControlApi _api;

    public WindowsDiskIoTraceSessionController()
        : this(WindowsDiskIoNativeTraceControlApi.Instance)
    {
    }

    internal WindowsDiskIoTraceSessionController(IWindowsDiskIoTraceControlApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public WindowsDiskIoTraceSessionStartResult Start()
    {
        using var properties = WindowsDiskIoTracePropertiesBuffer.CreateForStart();
        var status = _api.StartTrace(
            out var traceId,
            WindowsDiskIoSystemSessionPolicy.SessionName,
            properties.Pointer);

        if (status == WindowsDiskIoSystemSessionPolicy.ErrorSuccess)
        {
            if (traceId == 0)
            {
                throw new InvalidDataException(
                    "StartTraceW reported success but returned an invalid zero control-trace ID.");
            }

            return WindowsDiskIoTraceSessionStartResult.Success(
                new WindowsDiskIoOwnedTraceSession(_api, traceId));
        }

        if (WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStartFailure(
            status,
            out var failure))
        {
            return WindowsDiskIoTraceSessionStartResult.Unavailable(failure!);
        }

        throw CreateNativeFailure(status, "start the FileOp disk-I/O ETW session");
    }

    internal static Win32Exception CreateNativeFailure(uint status, string operation) =>
        new(
            unchecked((int)status),
            $"Could not {operation}: Win32 error {status} ({new Win32Exception(unchecked((int)status)).Message}).");
}

internal sealed class WindowsDiskIoOwnedTraceSession : IDisposable
{
    private readonly IWindowsDiskIoTraceControlApi _api;
    private readonly object _gate = new();
    private WindowsDiskIoSessionStopDisposition? _stopDisposition;
    private Exception? _terminalStopFailure;

    internal WindowsDiskIoOwnedTraceSession(
        IWindowsDiskIoTraceControlApi api,
        ulong traceId)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        if (traceId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(traceId),
                traceId,
                "Owned ETW sessions require a non-zero control-trace ID.");
        }

        TraceId = traceId;
    }

    public ulong TraceId { get; }

    public bool StopAttempted
    {
        get
        {
            lock (_gate)
            {
                return _stopDisposition.HasValue || _terminalStopFailure is not null;
            }
        }
    }

    public WindowsDiskIoSessionStopDisposition Stop()
    {
        lock (_gate)
        {
            if (_stopDisposition is { } stopped)
            {
                return stopped;
            }

            if (_terminalStopFailure is { } previousFailure)
            {
                throw new InvalidOperationException(
                    "The FileOp disk-I/O ETW session already had a terminal stop failure; FileOp will not issue an ambiguous second stop request.",
                    previousFailure);
            }

            using var properties = WindowsDiskIoTracePropertiesBuffer.CreateForStop();
            var status = _api.StopTrace(TraceId, properties.Pointer);
            if (WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStopResult(
                status,
                out var disposition))
            {
                _stopDisposition = disposition;
                return disposition;
            }

            var failure = WindowsDiskIoTraceSessionController.CreateNativeFailure(
                status,
                "stop the FileOp disk-I/O ETW session");
            _terminalStopFailure = failure;
            throw failure;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopDisposition.HasValue || _terminalStopFailure is not null)
            {
                return;
            }
        }

        Stop();
    }
}

internal sealed class WindowsDiskIoNativeTraceControlApi : IWindowsDiskIoTraceControlApi
{
    public static WindowsDiskIoNativeTraceControlApi Instance { get; } = new();

    private const string NativeLibrary = "sechost.dll";
    private const uint EventTraceControlStop = 1;

    private WindowsDiskIoNativeTraceControlApi()
    {
    }

    public uint StartTrace(
        out ulong traceId,
        string instanceName,
        IntPtr properties) =>
        StartTraceW(out traceId, instanceName, properties);

    public uint StopTrace(
        ulong traceId,
        IntPtr properties) =>
        ControlTraceW(
            traceId,
            instanceName: null,
            properties,
            EventTraceControlStop);

    [DllImport(
        NativeLibrary,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern uint StartTraceW(
        out ulong traceId,
        string instanceName,
        IntPtr properties);

    [DllImport(
        NativeLibrary,
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern uint ControlTraceW(
        ulong traceId,
        string? instanceName,
        IntPtr properties,
        uint controlCode);
}
