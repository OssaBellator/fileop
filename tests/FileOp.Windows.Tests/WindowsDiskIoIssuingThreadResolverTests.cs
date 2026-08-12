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
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        Assert.IsTrue(resolver.Resolve(100, EventTime.AddMilliseconds(1).ToFileTime()).Resolved);

        Assert.AreEqual(1, api.OpenThreadCalls);
        Assert.AreEqual(1, api.OpenProcessCalls);
    }

    [TestMethod]
    public void DifferentThreadsInSameProcessReusePinnedProcessHandle()
    {
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
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
        Assert.AreEqual(1, api.ClosedHandles.Count(handle => api.IsThreadHandle(handle)));
        Assert.AreEqual(1, api.ClosedHandles.Count(handle => api.IsProcessHandle(handle)));
    }

    [TestMethod]
    public void ImageLookupFailureKeepsStableProcessIdentity()
    {
        var api = new WindowsDiskIoLifetimeFake();
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
    public void ThreadCacheLimitStopsOpeningAdditionalThreads()
    {
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(0, EventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.InvalidThreadId, result.Status);
        Assert.AreEqual(0, api.OpenThreadCalls);
    }

    [TestMethod]
    public void InvalidEventFileTimeFailsBeforeNativeLookup()
    {
        var api = new WindowsDiskIoLifetimeFake();
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        Assert.Throws<InvalidDataException>(() => resolver.Resolve(100, -1));
        Assert.AreEqual(0, api.OpenThreadCalls);
    }

    [TestMethod]
    public void CacheLimitsMustBePositive()
    {
        var api = new WindowsDiskIoLifetimeFake();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowsDiskIoIssuingThreadResolver(api, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WindowsDiskIoIssuingThreadResolver(api, 1, 0));
    }

    [TestMethod]
    public void ResolveAfterDisposeIsRejected()
    {
        var api = new WindowsDiskIoLifetimeFake();
        var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        resolver.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            resolver.Resolve(100, EventTime.ToFileTime()));
    }
}
