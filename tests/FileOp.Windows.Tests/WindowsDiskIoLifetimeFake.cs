using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

internal sealed class WindowsDiskIoLifetimeFake : IWindowsDiskIoLifetimeApi
{
    private readonly Dictionary<uint, FakeThread> _threads = [];
    private readonly Dictionary<uint, FakeProcess> _processes = [];
    private readonly Dictionary<IntPtr, uint> _threadByHandle = [];
    private readonly Dictionary<IntPtr, uint> _processByHandle = [];

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
        var handle = ThreadHandle(threadId);
        _threads[threadId] = new FakeThread(
            handle,
            processId,
            startedAt.ToFileTime(),
            creationTimeAvailable);
        _threadByHandle[handle] = threadId;
    }

    public void AddProcess(
        uint processId,
        DateTimeOffset startedAt,
        string? imageName,
        bool creationTimeAvailable = true)
    {
        var handle = ProcessHandle(processId);
        _processes[processId] = new FakeProcess(
            handle,
            startedAt.ToFileTime(),
            imageName,
            creationTimeAvailable);
        _processByHandle[handle] = processId;
    }

    public IntPtr OpenThread(uint threadId)
    {
        OpenThreadCalls++;
        return _threads.TryGetValue(threadId, out var thread)
            ? thread.Handle
            : IntPtr.Zero;
    }

    public bool TryGetThreadCreationFileTime(IntPtr threadHandle, out long creationFileTime)
    {
        if (!_threadByHandle.TryGetValue(threadHandle, out var threadId) ||
            !_threads.TryGetValue(threadId, out var thread) ||
            !thread.CreationTimeAvailable)
        {
            creationFileTime = 0;
            return false;
        }

        creationFileTime = thread.CreationFileTime;
        return true;
    }

    public uint GetProcessIdOfThread(IntPtr threadHandle) =>
        _threadByHandle.TryGetValue(threadHandle, out var threadId) &&
        _threads.TryGetValue(threadId, out var thread)
            ? thread.ProcessId
            : 0;

    public IntPtr OpenProcess(uint processId)
    {
        OpenProcessCalls++;
        return _processes.TryGetValue(processId, out var process)
            ? process.Handle
            : IntPtr.Zero;
    }

    public bool TryGetProcessCreationFileTime(IntPtr processHandle, out long creationFileTime)
    {
        if (!_processByHandle.TryGetValue(processHandle, out var processId) ||
            !_processes.TryGetValue(processId, out var process) ||
            !process.CreationTimeAvailable)
        {
            creationFileTime = 0;
            return false;
        }

        creationFileTime = process.CreationFileTime;
        return true;
    }

    public string? TryGetProcessImageName(IntPtr processHandle) =>
        _processByHandle.TryGetValue(processHandle, out var processId) &&
        _processes.TryGetValue(processId, out var process)
            ? process.ImageName
            : null;

    public void CloseHandle(IntPtr handle)
    {
        CloseAttempts.Add(handle);
        if (FailCloseHandle == handle)
        {
            throw new InvalidOperationException("synthetic close failure");
        }

        ClosedHandles.Add(handle);
    }

    public bool IsThreadHandle(IntPtr handle) => _threadByHandle.ContainsKey(handle);

    public bool IsProcessHandle(IntPtr handle) => _processByHandle.ContainsKey(handle);

    public IntPtr ThreadHandle(uint threadId) =>
        new(unchecked(0x100000L + threadId));

    public IntPtr ProcessHandle(uint processId) =>
        new(unchecked(0x200000L + processId));

    private sealed record FakeThread(
        IntPtr Handle,
        uint ProcessId,
        long CreationFileTime,
        bool CreationTimeAvailable);

    private sealed record FakeProcess(
        IntPtr Handle,
        long CreationFileTime,
        string? ImageName,
        bool CreationTimeAvailable);
}
