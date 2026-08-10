using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

public enum WindowsDiskIoSessionStopDisposition
{
    Stopped,
    AlreadyStopped,
    StopInProgress,
}

public sealed record WindowsDiskIoSessionFailure(
    DiskIoCaptureStatus Status,
    uint Win32Error,
    string Detail);

public static class WindowsDiskIoSystemSessionPolicy
{
    public const string SessionName = "FileOp Disk I/O Diagnostics";

    // Stable FileOp-owned identifier for the dedicated SystemTraceProvider session.
    public static readonly Guid SessionGuid =
        new("6def68d0-e21a-403d-9ba5-0dc373e02eb8");

    // evntrace.h logging-mode constants. FileOp deliberately uses a dedicated
    // SystemTraceProvider session instead of the deprecated NT Kernel Logger identity.
    public const uint EventTraceRealTimeMode = 0x00000100;
    public const uint EventTraceSystemLoggerMode = 0x02000000;

    // EVENT_TRACE_PROPERTIES.EnableFlags values.
    public const uint EventTraceFlagDiskIo = 0x00000100;
    public const uint EventTraceFlagNoSysConfig = 0x10000000;

    // WNODE_HEADER.ClientContext = 1 selects QueryPerformanceCounter timestamps.
    public const uint QueryPerformanceCounterClock = 1;

    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorAlreadyExists = 183;
    public const uint ErrorNoSystemResources = 1450;
    public const uint ErrorActiveConnections = 2402;
    public const uint ErrorWmiInstanceNotFound = 4201;

    public static uint LogFileMode => EventTraceRealTimeMode | EventTraceSystemLoggerMode;

    public static uint EnableFlags => EventTraceFlagDiskIo | EventTraceFlagNoSysConfig;

    public static bool UsesLegacyNtKernelLoggerIdentity => false;

    public static bool TryClassifyExpectedStartFailure(
        uint win32Error,
        out WindowsDiskIoSessionFailure? failure)
    {
        failure = win32Error switch
        {
            ErrorAccessDenied => new WindowsDiskIoSessionFailure(
                DiskIoCaptureStatus.PermissionRequired,
                win32Error,
                "Windows denied control of the disk-I/O system trace session. FileOp will not modify group membership or system trace policy automatically."),
            ErrorAlreadyExists => new WindowsDiskIoSessionFailure(
                DiskIoCaptureStatus.SessionUnavailable,
                win32Error,
                $"The FileOp disk-I/O ETW session name or GUID is already active. FileOp will not stop or reuse a session whose ownership it cannot prove."),
            ErrorNoSystemResources => new WindowsDiskIoSessionFailure(
                DiskIoCaptureStatus.SessionUnavailable,
                win32Error,
                "Windows has no available ETW/system-logger capacity for this capture. FileOp will not raise ETW logger limits or stop another component's trace session."),
            _ => null,
        };
        return failure is not null;
    }

    public static bool TryClassifyExpectedStopResult(
        uint win32Error,
        out WindowsDiskIoSessionStopDisposition disposition)
    {
        switch (win32Error)
        {
            case ErrorSuccess:
                disposition = WindowsDiskIoSessionStopDisposition.Stopped;
                return true;
            case ErrorWmiInstanceNotFound:
                disposition = WindowsDiskIoSessionStopDisposition.AlreadyStopped;
                return true;
            case ErrorActiveConnections:
                disposition = WindowsDiskIoSessionStopDisposition.StopInProgress;
                return true;
            default:
                disposition = default;
                return false;
        }
    }
}
