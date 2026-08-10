using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal enum WindowsDiskIoOwnerResolutionStatus
{
    Resolved,
    InvalidThreadId,
    ThreadCacheLimitReached,
    ThreadUnavailable,
    ThreadTimeUnavailable,
    ProcessCacheLimitReached,
    ProcessUnavailable,
    ProcessTimeUnavailable,
    ThreadStartedAfterEvent,
    ProcessStartedAfterEvent,
}

internal sealed record WindowsDiskIoOwnerResolution(
    WindowsDiskIoOwnerResolutionStatus Status,
    DateTimeOffset ObservationTimestamp,
    DiskIoProcessIdentity? Owner)
{
    public bool Resolved => Status == WindowsDiskIoOwnerResolutionStatus.Resolved;
}

internal interface IWindowsDiskIoLifetimeApi
{
    IntPtr OpenThread(uint threadId);

    bool TryGetThreadCreationFileTime(IntPtr threadHandle, out long creationFileTime);

    uint GetProcessIdOfThread(IntPtr threadHandle);

    IntPtr OpenProcess(uint processId);

    bool TryGetProcessCreationFileTime(IntPtr processHandle, out long creationFileTime);

    string? TryGetProcessImageName(IntPtr processHandle);

    void CloseHandle(IntPtr handle);
}

internal sealed class WindowsDiskIoIssuingThreadResolver : IDisposable
{
    public const int DefaultMaximumCachedThreads = 2_048;
    public const int DefaultMaximumCachedProcesses = 1_024;

    private readonly object _gate = new();
    private readonly IWindowsDiskIoLifetimeApi _api;
    private readonly int _maximumCachedThreads;
    private readonly int _maximumCachedProcesses;
    private readonly Dictionary<uint, ThreadLease> _threads = [];
    private readonly Dictionary<uint, ProcessLease> _processes = [];
    private bool _disposed;

    public WindowsDiskIoIssuingThreadResolver(
        int maximumCachedThreads = DefaultMaximumCachedThreads,
        int maximumCachedProcesses = DefaultMaximumCachedProcesses)
        : this(
            WindowsDiskIoNativeLifetimeApi.Instance,
            maximumCachedThreads,
            maximumCachedProcesses)
    {
    }

    internal WindowsDiskIoIssuingThreadResolver(
        IWindowsDiskIoLifetimeApi api,
        int maximumCachedThreads = DefaultMaximumCachedThreads,
        int maximumCachedProcesses = DefaultMaximumCachedProcesses)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        if (maximumCachedThreads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCachedThreads));
        }
        if (maximumCachedProcesses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCachedProcesses));
        }

        _maximumCachedThreads = maximumCachedThreads;
        _maximumCachedProcesses = maximumCachedProcesses;
    }

    public WindowsDiskIoOwnerResolution Resolve(
        uint issuingThreadId,
        long eventTimestampFileTime)
    {
        var observationTimestamp = ConvertEventTimestamp(eventTimestampFileTime);
        if (issuingThreadId == 0)
        {
            return Unresolved(
                WindowsDiskIoOwnerResolutionStatus.InvalidThreadId,
                observationTimestamp);
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_threads.TryGetValue(issuingThreadId, out var cachedThread))
            {
                return ValidateLifetime(cachedThread, observationTimestamp);
            }

            if (_threads.Count >= _maximumCachedThreads)
            {
                return Unresolved(
                    WindowsDiskIoOwnerResolutionStatus.ThreadCacheLimitReached,
                    observationTimestamp);
            }

            var threadHandle = _api.OpenThread(issuingThreadId);
            if (threadHandle == IntPtr.Zero)
            {
                return Unresolved(
                    WindowsDiskIoOwnerResolutionStatus.ThreadUnavailable,
                    observationTimestamp);
            }

            try
            {
                if (!_api.TryGetThreadCreationFileTime(
                    threadHandle,
                    out var threadCreationFileTime))
                {
                    return Unresolved(
                        WindowsDiskIoOwnerResolutionStatus.ThreadTimeUnavailable,
                        observationTimestamp);
                }

                var processId = _api.GetProcessIdOfThread(threadHandle);
                if (processId == 0)
                {
                    return Unresolved(
                        WindowsDiskIoOwnerResolutionStatus.ProcessUnavailable,
                        observationTimestamp);
                }

                var process = GetOrCreateProcess(processId, observationTimestamp, out var processFailure);
                if (process is null)
                {
                    return Unresolved(processFailure, observationTimestamp);
                }

                var thread = new ThreadLease(
                    issuingThreadId,
                    ConvertNativeFileTime(threadCreationFileTime, "thread creation"),
                    threadHandle,
                    process);
                _threads.Add(issuingThreadId, thread);
                threadHandle = IntPtr.Zero;
                return ValidateLifetime(thread, observationTimestamp);
            }
            finally
            {
                if (threadHandle != IntPtr.Zero)
                {
                    _api.CloseHandle(threadHandle);
                }
            }
        }
    }

    private ProcessLease? GetOrCreateProcess(
        uint processId,
        DateTimeOffset observationTimestamp,
        out WindowsDiskIoOwnerResolutionStatus failure)
    {
        if (_processes.TryGetValue(processId, out var cached))
        {
            failure = default;
            return cached;
        }

        if (_processes.Count >= _maximumCachedProcesses)
        {
            failure = WindowsDiskIoOwnerResolutionStatus.ProcessCacheLimitReached;
            return null;
        }

        var processHandle = _api.OpenProcess(processId);
        if (processHandle == IntPtr.Zero)
        {
            failure = WindowsDiskIoOwnerResolutionStatus.ProcessUnavailable;
            return null;
        }

        try
        {
            if (!_api.TryGetProcessCreationFileTime(
                processHandle,
                out var processCreationFileTime))
            {
                failure = WindowsDiskIoOwnerResolutionStatus.ProcessTimeUnavailable;
                return null;
            }

            var startedAt = ConvertNativeFileTime(
                processCreationFileTime,
                "process creation");
            if (startedAt > observationTimestamp)
            {
                failure = WindowsDiskIoOwnerResolutionStatus.ProcessStartedAfterEvent;
                return null;
            }

            var imageName = NormalizeImageName(
                _api.TryGetProcessImageName(processHandle));
            var process = new ProcessLease(
                processId,
                startedAt,
                imageName,
                processHandle);
            _processes.Add(processId, process);
            processHandle = IntPtr.Zero;
            failure = default;
            return process;
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                _api.CloseHandle(processHandle);
            }
        }
    }

    private static WindowsDiskIoOwnerResolution ValidateLifetime(
        ThreadLease thread,
        DateTimeOffset observationTimestamp)
    {
        if (thread.StartedAt > observationTimestamp)
        {
            return Unresolved(
                WindowsDiskIoOwnerResolutionStatus.ThreadStartedAfterEvent,
                observationTimestamp);
        }
        if (thread.Process.StartedAt > observationTimestamp)
        {
            return Unresolved(
                WindowsDiskIoOwnerResolutionStatus.ProcessStartedAfterEvent,
                observationTimestamp);
        }

        return new WindowsDiskIoOwnerResolution(
            WindowsDiskIoOwnerResolutionStatus.Resolved,
            observationTimestamp,
            new DiskIoProcessIdentity(
                checked((int)thread.Process.ProcessId),
                thread.Process.StartedAt,
                thread.Process.ImageName));
    }

    private static WindowsDiskIoOwnerResolution Unresolved(
        WindowsDiskIoOwnerResolutionStatus status,
        DateTimeOffset observationTimestamp) =>
        new(status, observationTimestamp, null);

    internal static DateTimeOffset ConvertEventTimestamp(long eventTimestampFileTime) =>
        ConvertNativeFileTime(eventTimestampFileTime, "ETW event");

    private static DateTimeOffset ConvertNativeFileTime(long fileTime, string valueName)
    {
        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"The {valueName} FILETIME value {fileTime} is outside the representable DateTimeOffset range.",
                exception);
        }
    }

    private static string? NormalizeImageName(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var fileName = Path.GetFileName(fullPath.Trim());
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Exception? firstFailure = null;
            foreach (var thread in _threads.Values)
            {
                try
                {
                    _api.CloseHandle(thread.Handle);
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                }
            }
            foreach (var process in _processes.Values)
            {
                try
                {
                    _api.CloseHandle(process.Handle);
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                }
            }
            _threads.Clear();
            _processes.Clear();

            if (firstFailure is not null)
            {
                throw new InvalidOperationException(
                    "One or more DiskIo lifetime handles could not be closed; FileOp attempted cleanup for every cached handle.",
                    firstFailure);
            }
        }
    }

    private sealed record ThreadLease(
        uint ThreadId,
        DateTimeOffset StartedAt,
        IntPtr Handle,
        ProcessLease Process);

    private sealed record ProcessLease(
        uint ProcessId,
        DateTimeOffset StartedAt,
        string? ImageName,
        IntPtr Handle);
}

internal sealed class WindowsDiskIoNativeLifetimeApi : IWindowsDiskIoLifetimeApi
{
    public static WindowsDiskIoNativeLifetimeApi Instance { get; } = new();

    internal const uint ThreadQueryLimitedInformation = 0x0800;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const int MaximumImagePathCharacters = 32_768;

    private const string Kernel32 = "kernel32.dll";

    private WindowsDiskIoNativeLifetimeApi()
    {
    }

    public IntPtr OpenThread(uint threadId) =>
        OpenThreadNative(
            ThreadQueryLimitedInformation,
            inheritHandle: false,
            threadId);

    public bool TryGetThreadCreationFileTime(
        IntPtr threadHandle,
        out long creationFileTime) =>
        TryGetCreationFileTime(
            threadHandle,
            GetThreadTimesNative,
            out creationFileTime);

    public uint GetProcessIdOfThread(IntPtr threadHandle) =>
        GetProcessIdOfThreadNative(threadHandle);

    public IntPtr OpenProcess(uint processId) =>
        OpenProcessNative(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);

    public bool TryGetProcessCreationFileTime(
        IntPtr processHandle,
        out long creationFileTime) =>
        TryGetCreationFileTime(
            processHandle,
            GetProcessTimesNative,
            out creationFileTime);

    public string? TryGetProcessImageName(IntPtr processHandle)
    {
        var path = new StringBuilder(MaximumImagePathCharacters);
        var size = checked((uint)path.Capacity);
        if (!QueryFullProcessImageNameW(
            processHandle,
            flags: 0,
            path,
            ref size))
        {
            return null;
        }

        return path.ToString();
    }

    public void CloseHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && !CloseHandleNative(handle))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not close a FileOp DiskIo lifetime handle: Win32 error {error}.");
        }
    }

    private static bool TryGetCreationFileTime(
        IntPtr handle,
        GetTimesDelegate getTimes,
        out long creationFileTime)
    {
        if (!getTimes(
            handle,
            out var creation,
            out _,
            out _,
            out _))
        {
            creationFileTime = 0;
            return false;
        }

        creationFileTime = creation.ToInt64();
        return true;
    }

    private delegate bool GetTimesDelegate(
        IntPtr handle,
        out FileTimeNative creationTime,
        out FileTimeNative exitTime,
        out FileTimeNative kernelTime,
        out FileTimeNative userTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTimeNative
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public readonly long ToInt64() =>
            unchecked((long)(((ulong)HighDateTime << 32) | LowDateTime));
    }

    [DllImport(Kernel32, EntryPoint = "OpenThread", SetLastError = true)]
    private static extern IntPtr OpenThreadNative(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint threadId);

    [DllImport(Kernel32, EntryPoint = "GetThreadTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimesNative(
        IntPtr threadHandle,
        out FileTimeNative creationTime,
        out FileTimeNative exitTime,
        out FileTimeNative kernelTime,
        out FileTimeNative userTime);

    [DllImport(Kernel32, EntryPoint = "GetProcessIdOfThread", SetLastError = true)]
    private static extern uint GetProcessIdOfThreadNative(IntPtr threadHandle);

    [DllImport(Kernel32, EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern IntPtr OpenProcessNative(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport(Kernel32, EntryPoint = "GetProcessTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimesNative(
        IntPtr processHandle,
        out FileTimeNative creationTime,
        out FileTimeNative exitTime,
        out FileTimeNative kernelTime,
        out FileTimeNative userTime);

    [DllImport(
        Kernel32,
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr processHandle,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DllImport(Kernel32, EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandleNative(IntPtr handle);
}
