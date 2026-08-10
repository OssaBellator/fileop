using System.Reflection;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoNativeTraceConsumerLifecycleGuardsTests
{
    [TestMethod]
    public void ReopenIsRejectedWhilePriorProcessTraceIsStillDraining()
    {
        var api = new WindowsDiskIoNativeTraceConsumerApi(new NoOpSink());
        typeof(WindowsDiskIoNativeTraceConsumerApi)
            .GetField("_processActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, true);

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            api.OpenRealtime(
                WindowsDiskIoSystemSessionPolicy.SessionName,
                WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode));

        StringAssert.Contains(exception.Message, "draining");
    }

    private sealed class NoOpSink : IWindowsDiskIoNativeTraceCallbackSink
    {
        public bool OnEventRecord(IntPtr eventRecord) => true;

        public bool OnBuffer(IntPtr logfile) => true;
    }
}
