using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoIssuingThreadResolverTests
{
    private static readonly DateTimeOffset EventTime =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ResolvesStableOwnerAndNormalizesImageName()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\Program Files\Worker\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(100, EventTime.ToFileTime());

        Assert.IsTrue(result.Resolved);
        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.Resolved, result.Status);
        Assert.AreEqual(EventTime, result.ObservationTimestamp);
        Assert.IsNotNull(result.Owner);
        Assert.AreEqual(50, result.Owner.ProcessId);
        Assert.AreEqual(EventTime.AddMinutes(-10), result.Owner.StartedAt);
        Assert.AreEqual("worker.exe", result.Owner.ImageName);
        Assert.IsTrue(result.Owner.HasStableInstanceIdentity);
        Assert.AreEqual(1, api.OpenThreadCalls);
        Assert.AreEqual(1, api.OpenProcessCalls);
    }

    [TestMethod]
    public void SameThreadReusesPinnedThreadAndProcessHandles()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var first = resolver.Resolve(100, EventTime.ToFileTime());
        var second = resolver.Resolve(100, EventTime.AddMilliseconds(1).ToFileTime());

        Assert.IsTrue(first.Resolved);
        Assert.IsTrue(second.Resolved);
        Assert.AreEqual(1, api.OpenThreadCalls);
        Assert.AreEqual(1, api.OpenProcessCalls);
    }

    [TestMethod]
    public void DifferentThreadsInSameProcessReusePinnedProcessHandle()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        api.AddThread(101, 50, EventTime.AddMinutes(-4));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        Assert.IsTrue(resolver.Resolve(101, EventTime.ToFileTime()).Resolved);

        Assert.AreEqual(2, api.OpenThreadCalls);
        Assert.AreEqual(1, api.OpenProcessCalls);
    }

    [TestMethod]
    public void ThreadCreatedAfterOldEventIsPinnedButNotMisattributed()
    {
        var api = new FakeLifetimeApi();
        var threadStart = EventTime.AddSeconds(1);
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe");
        api.AddThread(100, 50, threadStart);
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var oldEvent = resolver.Resolve(100, EventTime.ToFileTime());
        var newEvent = resolver.Resolve(100, EventTime.AddSeconds(2).ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ThreadStartedAfterEvent, oldEvent.Status);
        Assert.IsNull(oldEvent.Owner);
        Assert.IsTrue(newEvent.Resolved);
        Assert.AreEqual(1, api.OpenThreadCalls);
        Assert.AreEqual(1, api.OpenProcessCalls);
    }

    [TestMethod]
    public void ProcessCreatedAfterOldEventIsNotCachedAndLaterEventRetries()
    {
        var api = new FakeLifetimeApi();
        var processStart = EventTime.AddSeconds(1);
        api.AddProcess(50, processStart, @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var oldEvent = resolver.Resolve(100, EventTime.ToFileTime());
        var newEvent = resolver.Resolve(100, EventTime.AddSeconds(2).ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ProcessStartedAfterEvent, oldEvent.Status);
        Assert.IsNull(oldEvent.Owner);
        Assert.IsTrue(newEvent.Resolved);
        Assert.AreEqual(2, api.OpenThreadCalls);
        Assert.AreEqual(2, api.OpenProcessCalls);
        Assert.AreEqual(2, api.ClosedHandles.Count(handle => api.IsThreadHandle(handle)));
        Assert.AreEqual(1, api.ClosedHandles.Count(handle => api.IsProcessHandle(handle)));
    }

    [TestMethod]
    public void ImageLookupFailureKeepsStableProcessIdentity()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), imageName: null);
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(100, EventTime.ToFileTime());

        Assert.IsTrue(result.Resolved);
        Assert.IsNotNull(result.Owner);
        Assert.AreEqual(50, result.Owner.ProcessId);
        Assert.AreEqual(EventTime.AddMinutes(-10), result.Owner.StartedAt);
        Assert.IsNull(result.Owner.ImageName);
        Assert.IsTrue(result.Owner.HasStableInstanceIdentity);
    }

    [TestMethod]
    public void ThreadOpenFailureIsUnattributedWithoutProcessLookup()
    {
        var api = new FakeLifetimeApi();
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(100, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ThreadUnavailable, result.Status);
        Assert.IsNull(result.Owner);
        Assert.AreEqual(1, api.OpenThreadCalls);
        Assert.AreEqual(0, api.OpenProcessCalls);
    }

    [TestMethod]
    public void ThreadTimeFailureClosesTemporaryThreadHandle()
    {
        var api = new FakeLifetimeApi();
        api.AddThread(100, 50, EventTime.AddMinutes(-5), creationTimeAvailable: false);
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(100, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ThreadTimeUnavailable, result.Status);
        Assert.AreEqual(1, api.ClosedHandles.Count);
        Assert.IsTrue(api.IsThreadHandle(api.ClosedHandles[0]));
    }

    [TestMethod]
    public void MissingProcessIdClosesTemporaryThreadHandle()
    {
        var api = new FakeLifetimeApi();
        api.AddThread(100, 0, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(100, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ProcessUnavailable, result.Status);
        Assert.AreEqual(1, api.ClosedHandles.Count);
        Assert.IsTrue(api.IsThreadHandle(api.ClosedHandles[0]));
    }

    [TestMethod]
    public void ProcessOpenFailureClosesTemporaryThreadHandle()
    {
        var api = new FakeLifetimeApi();
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(100, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ProcessUnavailable, result.Status);
        Assert.AreEqual(1, api.ClosedHandles.Count);
        Assert.IsTrue(api.IsThreadHandle(api.ClosedHandles[0]));
    }

    [TestMethod]
    public void ProcessTimeFailureClosesTemporaryProcessAndThreadHandles()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe", creationTimeAvailable: false);
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(100, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ProcessTimeUnavailable, result.Status);
        Assert.AreEqual(2, api.ClosedHandles.Count);
        Assert.AreEqual(1, api.ClosedHandles.Count(handle => api.IsThreadHandle(handle)));
        Assert.AreEqual(1, api.ClosedHandles.Count(handle => api.IsProcessHandle(handle)));
    }

    [TestMethod]
    public void ThreadCacheLimitStopsOpeningAdditionalThreads()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        api.AddThread(101, 50, EventTime.AddMinutes(-4));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(
            api,
            maximumCachedThreads: 1,
            maximumCachedProcesses: 10);

        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        var capped = resolver.Resolve(101, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ThreadCacheLimitReached, capped.Status);
        Assert.AreEqual(1, api.OpenThreadCalls);
    }

    [TestMethod]
    public void ProcessCacheLimitClosesNewThreadWithoutOpeningSecondProcess()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\one.exe");
        api.AddProcess(51, EventTime.AddMinutes(-10), @"C:\two.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        api.AddThread(101, 51, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(
            api,
            maximumCachedThreads: 10,
            maximumCachedProcesses: 1);

        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        var capped = resolver.Resolve(101, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ProcessCacheLimitReached, capped.Status);
        Assert.AreEqual(2, api.OpenThreadCalls);
        Assert.AreEqual(1, api.OpenProcessCalls);
        Assert.AreEqual(1, api.ClosedHandles.Count(handle => api.IsThreadHandle(handle)));
    }

    [TestMethod]
    public void ZeroThreadIdIsRejectedBeforeNativeLookup()
    {
        var api = new FakeLifetimeApi();
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(0, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.InvalidThreadId, result.Status);
        Assert.AreEqual(0, api.OpenThreadCalls);
    }

    [TestMethod]
    public void InvalidEventFileTimeFailsBeforeNativeLookup()
    {
        var api = new FakeLifetimeApi();
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        Assert.ThrowsException<InvalidDataException>(() => resolver.Resolve(100, -1));
        Assert.AreEqual(0, api.OpenThreadCalls);
    }

    [TestMethod]
    public void CacheLimitsMustBePositive()
    {
        var api = new FakeLifetimeApi();
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new WindowsDiskIoIssuingThreadResolver(api, 0, 1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new WindowsDiskIoIssuingThreadResolver(api, 1, 0));
    }

    [TestMethod]
    public void DisposeClosesEveryPinnedHandleExactlyOnceAndIsIdempotent()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        api.AddThread(101, 50, EventTime.AddMinutes(-4));
        var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        Assert.IsTrue(resolver.Resolve(101, EventTime.ToFileTime()).Resolved);

        resolver.Dispose();
        resolver.Dispose();

        Assert.AreEqual(3, api.ClosedHandles.Count);
        CollectionAssert.AreEquivalent(
            new[] { api.ThreadHandle(100), api.ThreadHandle(101), api.ProcessHandle(50) },
            api.ClosedHandles.ToArray());
    }

    [TestMethod]
    public void DisposeAttemptsEveryHandleEvenWhenOneCloseFails()
    {
        var api = new FakeLifetimeApi();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        api.AddThread(101, 50, EventTime.AddMinutes(-4));
        var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        Assert.IsTrue(resolver.Resolve(101, EventTime.ToFileTime()).Resolved);
        api.FailCloseHandle = api.ThreadHandle(100);

        Assert.ThrowsException<InvalidOperationException>(() => resolver.Dispose());

        Assert.AreEqual(3, api.CloseAttempts.Count);
        CollectionAssert.AreEquivalent(
            new[] { api.ThreadHandle(100), api.ThreadHandle(101), api.ProcessHandle(50) },
            api.CloseAttempts.ToArray());
    }

    [TestMethod]
    public void ResolveAfterDisposeIsRejected()
    {
        var api = new FakeLifetimeApi();
        var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        resolver.Dispose();

        Assert.ThrowsException<ObjectDisposedException>(() =>
            resolver.Resolve(100, EventTime.ToFileTime()));
    }

    [TestMethod]
    public void FileTimeNativeCombinesUnsignedHighAndLowWords()
    {
        var value = new WindowsDiskIoNativeLifetimeApi.FileTimeNative
        {
            HighDateTime = 0x01234567,
            LowDateTime = 0x89ABCDEF,
        };

        Assert.AreEqual(0x0123456789ABCDEFL, value.ToInt64());
    }

    private sealed class FakeLifetimeApi : IWindowsDiskIoLifetimeApi
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

        public IntPtr ThreadHandle(uint threadId) => new(0x100000 + checked((int)threadId));

        public IntPtr ProcessHandle(uint processId) => new(0x200000 + checked((int)processId));

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
}
