using System.Reflection;
using System.Runtime.InteropServices;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoNativeTraceConsumerApiTests
{
    [TestMethod]
    public void NativeStructSizesAndCallbackOffsetsMatchWindowsAbi()
    {
        Assert.AreEqual(
            48,
            Marshal.SizeOf<WindowsDiskIoNativeTraceConsumerApi.EventTraceHeaderNative>());
        Assert.AreEqual(
            IntPtr.Size == 8 ? 88 : 84,
            Marshal.SizeOf<WindowsDiskIoNativeTraceConsumerApi.EventTraceNative>());
        Assert.AreEqual(
            172,
            Marshal.SizeOf<WindowsDiskIoNativeTraceConsumerApi.TimeZoneInformationNative>());
        Assert.AreEqual(
            IntPtr.Size == 8 ? 280 : 272,
            Marshal.SizeOf<WindowsDiskIoNativeTraceConsumerApi.TraceLogfileHeaderNative>());
        Assert.AreEqual(
            IntPtr.Size == 8 ? 448 : 416,
            Marshal.SizeOf<WindowsDiskIoNativeTraceConsumerApi.EventTraceLogfileWNative>());

        Assert.AreEqual(
            IntPtr.Size == 8 ? 400 : 384,
            Marshal.OffsetOf<WindowsDiskIoNativeTraceConsumerApi.EventTraceLogfileWNative>(
                nameof(WindowsDiskIoNativeTraceConsumerApi.EventTraceLogfileWNative.BufferCallback)).ToInt32());
        Assert.AreEqual(
            IntPtr.Size == 8 ? 424 : 400,
            Marshal.OffsetOf<WindowsDiskIoNativeTraceConsumerApi.EventTraceLogfileWNative>(
                nameof(WindowsDiskIoNativeTraceConsumerApi.EventTraceLogfileWNative.EventRecordCallback)).ToInt32());
        Assert.AreEqual(
            IntPtr.Size == 8 ? 440 : 408,
            Marshal.OffsetOf<WindowsDiskIoNativeTraceConsumerApi.EventTraceLogfileWNative>(
                nameof(WindowsDiskIoNativeTraceConsumerApi.EventTraceLogfileWNative.Context)).ToInt32());
    }

    [TestMethod]
    public void TraceHeaderPerfFrequencyOffsetMatchesPointerWidth()
    {
        Assert.AreEqual(
            IntPtr.Size == 8 ? 256 : 248,
            Marshal.OffsetOf<WindowsDiskIoNativeTraceConsumerApi.TraceLogfileHeaderNative>(
                nameof(WindowsDiskIoNativeTraceConsumerApi.TraceLogfileHeaderNative.PerfFreq)).ToInt32());
        Assert.AreEqual(
            IntPtr.Size == 8 ? 276 : 268,
            Marshal.OffsetOf<WindowsDiskIoNativeTraceConsumerApi.TraceLogfileHeaderNative>(
                nameof(WindowsDiskIoNativeTraceConsumerApi.TraceLogfileHeaderNative.BuffersLost)).ToInt32());
    }

    [TestMethod]
    public void EventCallbackFalseRequestsCancellationAndSuppressesLaterCallbacks()
    {
        var sink = new RecordingSink { ContinueEvents = false };
        var api = new WindowsDiskIoNativeTraceConsumerApi(sink);

        InvokeEventCallback(api, new IntPtr(123));
        InvokeEventCallback(api, new IntPtr(456));

        Assert.AreEqual(1, sink.EventCalls);
        Assert.AreEqual(0u, InvokeBufferCallback(api, new IntPtr(789)));
        Assert.AreEqual(0, sink.BufferCalls);
    }

    [TestMethod]
    public void EventCallbackExceptionNeverCrossesCallbackAndIsRethrownLater()
    {
        var expected = new InvalidOperationException("callback fault");
        var sink = new RecordingSink { EventException = expected };
        var api = new WindowsDiskIoNativeTraceConsumerApi(sink);

        InvokeEventCallback(api, new IntPtr(123));

        Assert.AreEqual(0u, InvokeBufferCallback(api, new IntPtr(789)));
        var actual = Assert.ThrowsException<TargetInvocationException>(() =>
            InvokeThrowCallbackFault(api));
        Assert.AreSame(expected, actual.InnerException);
    }

    [TestMethod]
    public void NullEventRecordFailsClosedWithoutCallingSink()
    {
        var sink = new RecordingSink();
        var api = new WindowsDiskIoNativeTraceConsumerApi(sink);

        InvokeEventCallback(api, IntPtr.Zero);

        Assert.AreEqual(0, sink.EventCalls);
        Assert.AreEqual(0u, InvokeBufferCallback(api, new IntPtr(789)));
        Assert.ThrowsException<TargetInvocationException>(() =>
            InvokeThrowCallbackFault(api));
    }

    [TestMethod]
    public void BufferCallbackExceptionReturnsFalseAndPreservesOriginalFault()
    {
        var expected = new IOException("buffer fault");
        var sink = new RecordingSink { BufferException = expected };
        var api = new WindowsDiskIoNativeTraceConsumerApi(sink);

        Assert.AreEqual(0u, InvokeBufferCallback(api, new IntPtr(789)));
        var actual = Assert.ThrowsException<TargetInvocationException>(() =>
            InvokeThrowCallbackFault(api));
        Assert.AreSame(expected, actual.InnerException);
    }

    [TestMethod]
    public void BufferCallbackTrueContinuesProcessing()
    {
        var sink = new RecordingSink { ContinueBuffers = true };
        var api = new WindowsDiskIoNativeTraceConsumerApi(sink);

        Assert.AreEqual(1u, InvokeBufferCallback(api, new IntPtr(789)));
        Assert.AreEqual(1, sink.BufferCalls);
    }

    private static void InvokeEventCallback(
        WindowsDiskIoNativeTraceConsumerApi api,
        IntPtr eventRecord) =>
        GetPrivateMethod("EventRecordCallback").Invoke(api, new object[] { eventRecord });

    private static uint InvokeBufferCallback(
        WindowsDiskIoNativeTraceConsumerApi api,
        IntPtr logfile) =>
        (uint)GetPrivateMethod("BufferCallback").Invoke(api, new object[] { logfile })!;

    private static void InvokeThrowCallbackFault(WindowsDiskIoNativeTraceConsumerApi api)
    {
        var field = typeof(WindowsDiskIoNativeTraceConsumerApi).GetField(
            "_callbackFault",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fault = (System.Runtime.ExceptionServices.ExceptionDispatchInfo?)field.GetValue(api);
        fault?.Throw();
    }

    private static MethodInfo GetPrivateMethod(string name) =>
        typeof(WindowsDiskIoNativeTraceConsumerApi).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class RecordingSink : IWindowsDiskIoNativeTraceCallbackSink
    {
        public bool ContinueEvents { get; init; } = true;
        public bool ContinueBuffers { get; init; } = true;
        public Exception? EventException { get; init; }
        public Exception? BufferException { get; init; }
        public int EventCalls { get; private set; }
        public int BufferCalls { get; private set; }

        public bool OnEventRecord(IntPtr eventRecord)
        {
            EventCalls++;
            if (EventException is not null)
            {
                throw EventException;
            }

            return ContinueEvents;
        }

        public bool OnBuffer(IntPtr logfile)
        {
            BufferCalls++;
            if (BufferException is not null)
            {
                throw BufferException;
            }

            return ContinueBuffers;
        }
    }
}
