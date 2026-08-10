using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoIssuingThreadResolverNativeIdTests
{
    [TestMethod]
    public void ProcessIdOutsideCoreIdentityRangeIsUnattributedBeforeOpenProcess()
    {
        var eventTime = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        var api = new WindowsDiskIoLifetimeFake();
        api.AddThread(
            threadId: 100,
            processId: (uint)int.MaxValue + 1u,
            startedAt: eventTime.AddMinutes(-1));
        using var resolver = new WindowsDiskIoIssuingThreadResolver(api);

        var result = resolver.Resolve(100, eventTime.ToFileTime());

        Assert.AreEqual(WindowsDiskIoOwnerResolutionStatus.ProcessIdOutOfRange, result.Status);
        Assert.IsNull(result.Owner);
        Assert.AreEqual(1, api.OpenThreadCalls);
        Assert.AreEqual(0, api.OpenProcessCalls);
        Assert.AreEqual(1, api.ClosedHandles.Count);
        Assert.IsTrue(api.IsThreadHandle(api.ClosedHandles[0]));
    }
}
