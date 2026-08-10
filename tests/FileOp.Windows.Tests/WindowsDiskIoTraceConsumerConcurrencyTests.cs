using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoTraceConsumerConcurrencyTests
{
    [TestMethod]
    public void ProcessingHandleCanEnterProcessTraceOnlyOnce()
    {
        var api = new SingleShotFakeConsumerApi();
        using var consumer = new WindowsDiskIoTraceConsumerLifecycle(api).Open().Consumer!;

        Assert.AreEqual(
            WindowsDiskIoTraceProcessDisposition.Completed,
            consumer.Process());
        Assert.AreEqual(1, api.ProcessCalls);

        Assert.ThrowsException<InvalidOperationException>(() => consumer.Process());
        Assert.AreEqual(1, api.ProcessCalls);
    }

    private sealed class SingleShotFakeConsumerApi : IWindowsDiskIoTraceConsumerApi
    {
        public int ProcessCalls { get; private set; }

        public WindowsDiskIoNativeOpenResult OpenRealtime(
            string sessionName,
            uint processTraceMode) =>
            new(555, WindowsDiskIoTraceConsumerPolicy.ErrorSuccess);

        public uint ProcessTrace(ulong processingHandle)
        {
            ProcessCalls++;
            return WindowsDiskIoTraceConsumerPolicy.ErrorSuccess;
        }

        public uint CloseTrace(ulong processingHandle) =>
            WindowsDiskIoTraceConsumerPolicy.ErrorSuccess;
    }
}
