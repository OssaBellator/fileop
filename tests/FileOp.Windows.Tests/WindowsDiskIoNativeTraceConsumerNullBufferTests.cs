using System.Reflection;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoNativeTraceConsumerNullBufferTests
{
    [TestMethod]
    public void NullBufferCallbackFailsClosedWithoutCallingSink()
    {
        var sink = new RecordingSink();
        var api = new WindowsDiskIoNativeTraceConsumerApi(sink);

        var result = (uint)typeof(WindowsDiskIoNativeTraceConsumerApi)
            .GetMethod("BufferCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(api, new object[] { IntPtr.Zero })!;

        Assert.AreEqual(0u, result);
        Assert.AreEqual(0, sink.BufferCalls);

        var field = typeof(WindowsDiskIoNativeTraceConsumerApi).GetField(
            "_callbackFault",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fault = (System.Runtime.ExceptionServices.ExceptionDispatchInfo?)field.GetValue(api);
        Assert.IsNotNull(fault);
        Assert.ThrowsException<InvalidDataException>(() => fault.Throw());
    }

    private sealed class RecordingSink : IWindowsDiskIoNativeTraceCallbackSink
    {
        public int BufferCalls { get; private set; }

        public bool OnEventRecord(IntPtr eventRecord) => true;

        public bool OnBuffer(IntPtr logfile)
        {
            BufferCalls++;
            return true;
        }
    }
}
