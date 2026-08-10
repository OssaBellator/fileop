using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal enum WindowsDiskIoTraceProcessDisposition
{
    Completed,
    CancelledByConsumer,
    CollectionSessionEnded,
}

internal enum WindowsDiskIoTraceCloseDisposition
{
    Closed,
    ClosePending,
}

internal sealed record WindowsDiskIoTraceOpenFailure(
    DiskIoCaptureStatus Status,
    uint Win32Error,
    string Detail);

internal readonly record struct WindowsDiskIoNativeOpenResult(
    ulong ProcessingHandle,
    uint Win32Error)
{
    public bool Succeeded => Win32Error == WindowsDiskIoTraceConsumerPolicy.ErrorSuccess;
}

internal interface IWindowsDiskIoTraceConsumerApi
{
    WindowsDiskIoNativeOpenResult OpenRealtime(
        string sessionName,
        uint processTraceMode);

    uint ProcessTrace(ulong processingHandle);

    uint CloseTrace(ulong processingHandle);
}

internal static class WindowsDiskIoTraceConsumerPolicy
{
    public const uint ProcessTraceModeRealTime = 0x00000100;
    public const uint ProcessTraceModeEventRecord = 0x10000000;

    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorInvalidHandle = 6;
    public const uint ErrorNoAccess = 998;
    public const uint ErrorCancelled = 1223;
    public const uint ErrorWmiInstanceNotFound = 4201;
    public const uint ErrorCtxClosePending = 7007;

    public static uint ProcessTraceMode =>
        ProcessTraceModeRealTime | ProcessTraceModeEventRecord;

    public static ulong InvalidProcessTraceHandle => IntPtr.Size == 8
        ? ulong.MaxValue
        : uint.MaxValue;

    public static bool IsInvalidProcessingHandle(ulong handle) =>
        handle == InvalidProcessTraceHandle;

    public static bool TryClassifyExpectedOpenFailure(
        uint win32Error,
        out WindowsDiskIoTraceOpenFailure? failure)
    {
        failure = win32Error switch
        {
            ErrorAccessDenied => new WindowsDiskIoTraceOpenFailure(
                DiskIoCaptureStatus.PermissionRequired,
                win32Error,
                "Windows denied real-time access to the FileOp disk-I/O ETW session. FileOp will not change ETW ACLs or privileged-group membership automatically."),
            ErrorWmiInstanceNotFound => new WindowsDiskIoTraceOpenFailure(
                DiskIoCaptureStatus.SessionUnavailable,
                win32Error,
                "The FileOp disk-I/O collection session is no longer running or is not available for real-time consumption."),
            _ => null,
        };
        return failure is not null;
    }

    public static bool TryClassifyExpectedProcessResult(
        uint win32Error,
        out WindowsDiskIoTraceProcessDisposition disposition)
    {
        switch (win32Error)
        {
            case ErrorSuccess:
                disposition = WindowsDiskIoTraceProcessDisposition.Completed;
                return true;
            case ErrorCancelled:
                disposition = WindowsDiskIoTraceProcessDisposition.CancelledByConsumer;
                return true;
            case ErrorWmiInstanceNotFound:
                disposition = WindowsDiskIoTraceProcessDisposition.CollectionSessionEnded;
                return true;
            default:
                disposition = default;
                return false;
        }
    }

    public static bool TryClassifyExpectedCloseResult(
        uint win32Error,
        out WindowsDiskIoTraceCloseDisposition disposition)
    {
        switch (win32Error)
        {
            case ErrorSuccess:
                disposition = WindowsDiskIoTraceCloseDisposition.Closed;
                return true;
            case ErrorCtxClosePending:
                disposition = WindowsDiskIoTraceCloseDisposition.ClosePending;
                return true;
            default:
                disposition = default;
                return false;
        }
    }
}

internal sealed record WindowsDiskIoTraceConsumerOpenResult(
    WindowsDiskIoOwnedTraceConsumer? Consumer,
    WindowsDiskIoTraceOpenFailure? Failure)
{
    public bool Opened => Consumer is not null;

    public static WindowsDiskIoTraceConsumerOpenResult Success(
        WindowsDiskIoOwnedTraceConsumer consumer) =>
        new(consumer, null);

    public static WindowsDiskIoTraceConsumerOpenResult Unavailable(
        WindowsDiskIoTraceOpenFailure failure) =>
        new(null, failure);
}

internal sealed class WindowsDiskIoTraceConsumerLifecycle
{
    private readonly IWindowsDiskIoTraceConsumerApi _api;

    internal WindowsDiskIoTraceConsumerLifecycle(IWindowsDiskIoTraceConsumerApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public WindowsDiskIoTraceConsumerOpenResult Open()
    {
        var result = _api.OpenRealtime(
            WindowsDiskIoSystemSessionPolicy.SessionName,
            WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode);
        if (result.Succeeded)
        {
            if (WindowsDiskIoTraceConsumerPolicy.IsInvalidProcessingHandle(
                result.ProcessingHandle))
            {
                throw new InvalidDataException(
                    "OpenTrace reported success but returned INVALID_PROCESSTRACE_HANDLE.");
            }

            return WindowsDiskIoTraceConsumerOpenResult.Success(
                new WindowsDiskIoOwnedTraceConsumer(_api, result.ProcessingHandle));
        }

        if (WindowsDiskIoTraceConsumerPolicy.TryClassifyExpectedOpenFailure(
            result.Win32Error,
            out var failure))
        {
            return WindowsDiskIoTraceConsumerOpenResult.Unavailable(failure!);
        }

        throw WindowsDiskIoTraceSessionController.CreateNativeFailure(
            result.Win32Error,
            "open the FileOp disk-I/O ETW real-time consumer");
    }
}

internal sealed class WindowsDiskIoOwnedTraceConsumer : IDisposable
{
    private readonly IWindowsDiskIoTraceConsumerApi _api;
    private readonly object _gate = new();
    private WindowsDiskIoTraceCloseDisposition? _closeDisposition;
    private Exception? _terminalCloseFailure;

    internal WindowsDiskIoOwnedTraceConsumer(
        IWindowsDiskIoTraceConsumerApi api,
        ulong processingHandle)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        if (WindowsDiskIoTraceConsumerPolicy.IsInvalidProcessingHandle(processingHandle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(processingHandle),
                processingHandle,
                "Owned ETW consumers require a valid processing handle.");
        }

        ProcessingHandle = processingHandle;
    }

    public ulong ProcessingHandle { get; }

    public WindowsDiskIoTraceProcessDisposition Process()
    {
        lock (_gate)
        {
            if (_closeDisposition.HasValue)
            {
                throw new ObjectDisposedException(
                    nameof(WindowsDiskIoOwnedTraceConsumer),
                    "The ETW processing handle is already closing or closed.");
            }

            if (_terminalCloseFailure is not null)
            {
                throw new InvalidOperationException(
                    "The ETW processing handle had a terminal close failure and will not be reused.",
                    _terminalCloseFailure);
            }
        }

        var status = _api.ProcessTrace(ProcessingHandle);
        if (WindowsDiskIoTraceConsumerPolicy.TryClassifyExpectedProcessResult(
            status,
            out var disposition))
        {
            return disposition;
        }

        throw WindowsDiskIoTraceSessionController.CreateNativeFailure(
            status,
            "process the FileOp disk-I/O ETW real-time consumer");
    }

    public WindowsDiskIoTraceCloseDisposition Close()
    {
        lock (_gate)
        {
            if (_closeDisposition is { } closed)
            {
                return closed;
            }

            if (_terminalCloseFailure is { } previousFailure)
            {
                throw new InvalidOperationException(
                    "The ETW processing handle already had a terminal close failure; FileOp will not issue an ambiguous second CloseTrace call.",
                    previousFailure);
            }

            var status = _api.CloseTrace(ProcessingHandle);
            if (WindowsDiskIoTraceConsumerPolicy.TryClassifyExpectedCloseResult(
                status,
                out var disposition))
            {
                _closeDisposition = disposition;
                return disposition;
            }

            var failure = WindowsDiskIoTraceSessionController.CreateNativeFailure(
                status,
                "close the FileOp disk-I/O ETW processing handle");
            _terminalCloseFailure = failure;
            throw failure;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closeDisposition.HasValue || _terminalCloseFailure is not null)
            {
                return;
            }
        }

        Close();
    }
}
