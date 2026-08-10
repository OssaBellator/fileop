using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

internal sealed class WindowsDiskIoLifetimeFake : IWindowsDiskIoLifetimeApi
{
    private readonly Dictionary<uint, IntPtr> _currentThreads = [];
    private readonly Dictionary<uint, IntPtr> _currentProcesses = [];
    private readonly Dictionary<IntPtr, FakeThread> _threadsByHandle = [];
    private readonly Dictionary<IntPtr, FakeProcess> _processesByHandle = [];
    private readonly Dictionary<IntPtr, WindowsDiskIoObjectState> _states = [];
    private long _nextThreadHandle = 0x100000;
    private long _nextProcessHandle = 0x200000;

    public int OpenThreadCalls { get; private set; }
    public int OpenProcessCalls { get; private set; }
    public List<IntPtr> CloseAttempts { get; } = [];
    public List<IntPtr> ClosedHandles { get; } = [];
    public IntPtr? FailCloseHandle { get; set; }

    public void AddThread(
        uint threadId,
        uint processId,
        DateTimeOffset startedAt,
        bool creationTimeAvailable = true)
    {
        var handle = new IntPtr(_nextThreadHandle++);
        _currentThreads[threadId] = handle;
        _threadsByHandle[handle] = new FakeThread(
            threadId,
            processId,
            startedAt.ToFileTime(),
            creationTimeAvailable);
        _states[handle] = WindowsDiskIoObjectState.Active;
    }

    public void AddProcess(
        uint processId,
        DateTimeOffset startedAt,
        string? imageName,
        bool creationTimeAvailable = true)
    {
        var handle = new IntPtr(_nextProcessHandle++);
        _currentProcesses[processId] = handle;
        _processesByHandle[handle] = new FakeProcess(
            processId,
            startedAt.ToFileTime(),
            imageName,
            creationTimeAvailable);
        _states[handle] = WindowsDiskIoObjectState.Active;
    }

    public void MarkInactive(IntPtr handle)
    {
        if (!_states.ContainsKey(handle))
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }

        _states[handle] = WindowsDiskIoObjectState.Terminated;
    }

    public void MarkStateIndeterminate(IntPtr handle)
    {
        if (!_states.ContainsKey(handle))
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }

        _states[handle] = WindowsDiskIoObjectState.Unavailable;
    }

    public IntPtr OpenThread(uint threadId)
    {
        OpenThreadCalls++;
        return _currentThreads.TryGetValue(threadId, out var handle)
            ? handle
            : IntPtr.Zero;
    }

    public bool TryGetThreadCreationFileTime(IntPtr threadHandle, out long creationFileTime)
    {
        if (!_threadsByHandle.TryGetValue(threadHandle, out var thread) ||
            !thread.CreationTimeAvailable)
        {
            creationFileTime = 0;
            return false;
        }

        creationFileTime = thread.CreationFileTime;
        return true;
    }

    public uint GetProcessIdOfThread(IntPtr threadHandle) =>
        _threadsByHandle.TryGetValue(threadHandle, out var thread)
            ? thread.ProcessId
            : 0;

    public IntPtr OpenProcess(uint processId)
    {
        OpenProcessCalls++;
        return _currentProcesses.TryGetValue(processId, out var handle)
            ? handle
            : IntPtr.Zero;
    }

    public bool TryGetProcessCreationFileTime(IntPtr processHandle, out long creationFileTime)
    {
        if (!_processesByHandle.TryGetValue(processHandle, out var process) ||
            !process.CreationTimeAvailable)
        {
            creationFileTime = 0;
            return false;
        }

        creationFileTime = process.CreationFileTime;
        return true;
    }

    public string? TryGetProcessImageName(IntPtr processHandle) =>
        _processesByHandle.TryGetValue(processHandle, out var process)
            ? process.ImageName
            : null;

    public WindowsDiskIoObjectState GetObjectState(IntPtr handle) =>
        _states.TryGetValue(handle, out var state)
            ? state
            : WindowsDiskIoObjectState.Unavailable;

    public void CloseHandle(IntPtr handle)
    {
        CloseAttempts.Add(handle);
        if (FailCloseHandle == handle)
        {
            throw new InvalidOperationException("synthetic close failure");
        }

        ClosedHandles.Add(handle);
    }

    public bool IsThreadHandle(IntPtr handle) => _threadsByHandle.ContainsKey(handle);

    public bool IsProcessHandle(IntPtr handle) => _processesByHandle.ContainsKey(handle);

    public IntPtr ThreadHandle(uint threadId) =>
        _currentThreads.TryGetValue(threadId, out var handle)
            ? handle
            : IntPtr.Zero;

    public IntPtr ProcessHandle(uint processId) =>
        _currentProcesses.TryGetValue(processId, out var handle)
            ? handle
            : IntPtr.Zero;

    private sealed record FakeThread(
        uint ThreadId,
        uint ProcessId,
        long CreationFileTime,
        bool CreationTimeAvailable);

    private sealed record FakeProcess(
        uint ProcessId,
        long CreationFileTime,
        string? ImageName,
        bool CreationTimeAvailable);
}
