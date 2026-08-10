using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoIssuingThreadResolverReuseTests
{
    private static readonly DateTimeOffset EventTime =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TerminatedCachedThreadIsEvictedBeforeSameTidIsResolvedAgain()
    {
        var api = new WindowsDiskIoLifetimeFake();
        api.AddProcess(50, EventTime.AddMinutes(-20), @"C:\old.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-10));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var first = resolver.Resolve(100, EventTime.ToFileTime());
        Assert.IsTrue(first.Resolved);
        Assert.AreEqual(50, first.Owner!.ProcessId);
        var oldThreadHandle = api.ThreadHandle(100);

        api.MarkInactive(oldThreadHandle);
        api.AddProcess(51, EventTime.AddSeconds(1), @"C:\new.exe");
        api.AddThread(100, 51, EventTime.AddSeconds(2));

        var second = resolver.Resolve(100, EventTime.AddSeconds(3).ToFileTime());

        Assert.IsTrue(second.Resolved);
        Assert.AreEqual(51, second.Owner!.ProcessId);
        Assert.AreEqual("new.exe", second.Owner.ImageName);
        Assert.AreEqual(2, api.OpenThreadCalls);
        CollectionAssert.Contains(api.ClosedHandles, oldThreadHandle);
        Assert.AreNotEqual(oldThreadHandle, api.ThreadHandle(100));
    }

    [TestMethod]
    public void TerminatedCachedProcessIsEvictedBeforeSamePidIsReused()
    {
        var api = new WindowsDiskIoLifetimeFake();
        var oldStart = EventTime.AddMinutes(-20);
        api.AddProcess(50, oldStart, @"C:\old.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-10));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        var oldProcessHandle = api.ProcessHandle(50);

        api.MarkInactive(oldProcessHandle);
        var newStart = EventTime.AddSeconds(1);
        api.AddProcess(50, newStart, @"C:\new.exe");
        api.AddThread(101, 50, EventTime.AddSeconds(2));

        var second = resolver.Resolve(101, EventTime.AddSeconds(3).ToFileTime());

        Assert.IsTrue(second.Resolved);
        Assert.AreEqual(50, second.Owner!.ProcessId);
        Assert.AreEqual(newStart, second.Owner.StartedAt);
        Assert.AreEqual("new.exe", second.Owner.ImageName);
        Assert.AreEqual(2, api.OpenProcessCalls);
        CollectionAssert.Contains(api.ClosedHandles, oldProcessHandle);
        Assert.AreNotEqual(oldProcessHandle, api.ProcessHandle(50));
    }

    [TestMethod]
    public void OldQueuedEventIsNotAssignedToNewThreadThatReusedTid()
    {
        var api = new WindowsDiskIoLifetimeFake();
        api.AddProcess(50, EventTime.AddMinutes(-20), @"C:\old.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-10));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        var oldThreadHandle = api.ThreadHandle(100);

        api.MarkInactive(oldThreadHandle);
        api.AddProcess(51, EventTime.AddSeconds(10), @"C:\new.exe");
        api.AddThread(100, 51, EventTime.AddSeconds(11));

        var delayedOldEvent = resolver.Resolve(100, EventTime.AddSeconds(5).ToFileTime());

        Assert.AreEqual(
            WindowsDiskIoOwnerResolutionStatus.ProcessStartedAfterEvent,
            delayedOldEvent.Status);
        Assert.IsNull(delayedOldEvent.Owner);
    }

    [TestMethod]
    public void IndeterminateCachedThreadStateStaysUnattributedWithoutReopen()
    {
        var api = new WindowsDiskIoLifetimeFake();
        api.AddProcess(50, EventTime.AddMinutes(-20), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-10));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        api.MarkStateIndeterminate(api.ThreadHandle(100));

        var result = resolver.Resolve(100, EventTime.AddSeconds(1).ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ThreadStateUnavailable, result.Status);
        Assert.IsNull(result.Owner);
        Assert.AreEqual(1, api.OpenThreadCalls);
    }

    [TestMethod]
    public void IndeterminateCachedProcessStateStaysUnattributedAndClosesNewThread()
    {
        var api = new WindowsDiskIoLifetimeFake();
        api.AddProcess(50, EventTime.AddMinutes(-20), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-10));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);

        api.MarkStateIndeterminate(api.ProcessHandle(50));
        api.AddThread(101, 50, EventTime.AddMinutes(-5));
        var newThreadHandle = api.ThreadHandle(101);

        var result = resolver.Resolve(101, EventTime.AddSeconds(1).ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ProcessStateUnavailable, result.Status);
        Assert.IsNull(result.Owner);
        Assert.AreEqual(1, api.OpenProcessCalls);
        CollectionAssert.Contains(api.ClosedHandles, newThreadHandle);
    }
}
