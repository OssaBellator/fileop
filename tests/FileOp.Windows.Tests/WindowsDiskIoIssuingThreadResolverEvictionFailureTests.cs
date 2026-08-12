using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoIssuingThreadResolverEvictionFailureTests
{
    private static readonly DateTimeOffset EventTime =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void FailedTerminatedThreadCloseKeepsOldEntryForCleanupRetry()
    {
        var api = new WindowsDiskIoLifetimeFake();
        api.AddProcess(50, EventTime.AddMinutes(-20), @"C:\old.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-10));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        var oldThreadHandle = api.ThreadHandle(100);

        api.MarkInactive(oldThreadHandle);
        api.AddProcess(51, EventTime.AddSeconds(1), @"C:\new.exe");
        api.AddThread(100, 51, EventTime.AddSeconds(2));
        api.FailCloseHandle = oldThreadHandle;

        Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(100, EventTime.AddSeconds(3).ToFileTime()));
        Assert.AreEqual(1, api.OpenThreadCalls);
        Assert.AreEqual(1, api.CloseAttempts.Count(handle => handle == oldThreadHandle));

        api.FailCloseHandle = null;
        var retried = resolver.Resolve(100, EventTime.AddSeconds(3).ToFileTime());

        Assert.IsTrue(retried.Resolved);
        Assert.AreEqual(51, retried.Owner!.ProcessId);
        Assert.AreEqual(2, api.OpenThreadCalls);
        Assert.AreEqual(2, api.CloseAttempts.Count(handle => handle == oldThreadHandle));
    }

    [TestMethod]
    public void FailedTerminatedProcessCloseKeepsOldEntryForCleanupRetry()
    {
        var api = new WindowsDiskIoLifetimeFake();
        api.AddProcess(50, EventTime.AddMinutes(-20), @"C:\old.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-10));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        var oldProcessHandle = api.ProcessHandle(50);

        api.MarkInactive(oldProcessHandle);
        api.AddProcess(50, EventTime.AddSeconds(1), @"C:\new.exe");
        api.AddThread(101, 50, EventTime.AddSeconds(2));
        var currentThreadHandle = api.ThreadHandle(101);
        api.FailCloseHandle = oldProcessHandle;

        Assert.Throws<InvalidOperationException>(() =>
            resolver.Resolve(101, EventTime.AddSeconds(3).ToFileTime()));
        Assert.AreEqual(1, api.OpenProcessCalls);
        CollectionAssert.Contains(api.ClosedHandles, currentThreadHandle);
        Assert.AreEqual(1, api.CloseAttempts.Count(handle => handle == oldProcessHandle));

        api.FailCloseHandle = null;
        var retried = resolver.Resolve(101, EventTime.AddSeconds(3).ToFileTime());

        Assert.IsTrue(retried.Resolved);
        Assert.AreEqual("new.exe", retried.Owner!.ImageName);
        Assert.AreEqual(2, api.OpenProcessCalls);
        Assert.AreEqual(2, api.CloseAttempts.Count(handle => handle == oldProcessHandle));
    }
}
