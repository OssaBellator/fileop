using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoIssuingThreadResolverFailureTests
{
    private static readonly DateTimeOffset EventTime =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ThreadOpenFailureIsUnattributedWithoutProcessLookup()
    {
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
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
    public void DisposeClosesEveryPinnedHandleExactlyOnceAndIsIdempotent()
    {
        var api = new WindowsDiskIoLifetimeFake();
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
        var api = new WindowsDiskIoLifetimeFake();
        api.AddProcess(50, EventTime.AddMinutes(-10), @"C:\worker.exe");
        api.AddThread(100, 50, EventTime.AddMinutes(-5));
        api.AddThread(101, 50, EventTime.AddMinutes(-4));
        var resolver = new WindowsDiskIoIssuingThreadResolver(api);
        Assert.IsTrue(resolver.Resolve(100, EventTime.ToFileTime()).Resolved);
        Assert.IsTrue(resolver.Resolve(101, EventTime.ToFileTime()).Resolved);
        api.FailCloseHandle = api.ThreadHandle(100);

        Assert.Throws<InvalidOperationException>(() => resolver.Dispose());

        Assert.AreEqual(3, api.CloseAttempts.Count);
        CollectionAssert.AreEquivalent(
            new[] { api.ThreadHandle(100), api.ThreadHandle(101), api.ProcessHandle(50) },
            api.CloseAttempts.ToArray());
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
}
