using System.Reflection;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoNativeTraceConsumerApiTests
{
    [TestMethod]
    public void ExplicitLogfileLayoutMatchesWindowsAbi()
    {
        Assert.AreEqual(48, WindowsDiskIoTraceLogfileBuffer.EventTraceHeaderSize);
        Assert.AreEqual(88, WindowsDiskIoTraceLogfileBuffer.EventTraceSize);
        Assert.AreEqual(172, WindowsDiskIoTraceLogfileBuffer.TimeZoneInformationSize);
        Assert.AreEqual(
            IntPtr.Size == 8 ? 280 : 272,
            IntPtr.Size == 8
                ? WindowsDiskIoTraceLogfileBuffer.TraceLogfileHeaderSize64
                : WindowsDiskIoTraceLogfileBuffer.TraceLogfileHeaderSize32);
        Assert.AreEqual(
            IntPtr.Size == 8 ? 448 : 416,
            IntPtr.Size == 8
                ? WindowsDiskIoTraceLogfileBuffer.EventTraceLogfileSize64
                : WindowsDiskIoTraceLogfileBuffer.EventTraceLogfileSize32);

        Assert.AreEqual(IntPtr.Size == 8 ? 8 : 4, WindowsDiskIoTraceLogfileBuffer.LoggerNameOffset);
        Assert.AreEqual(IntPtr.Size == 8 ? 28 : 20, WindowsDiskIoTraceLogfileBuffer.ProcessTraceModeOffset);
        Assert.AreEqual(IntPtr.Size == 8 ? 400 : 384, WindowsDiskIoTraceLogfileBuffer.BufferCallbackOffset);
        Assert.AreEqual(IntPtr.Size == 8 ? 424 : 400, WindowsDiskIoTraceLogfileBuffer.EventRecordCallbackOffset);
        Assert.AreEqual(IntPtr.Size == 8 ? 440 : 408, WindowsDiskIoTraceLogfileBuffer.ContextOffset);
    }

    [TestMethod]
    public void TraceHeaderEvidenceOffsetsMatchPointerWidth()
    {
        Assert.AreEqual(
            IntPtr.Size == 8 ? 376 : 360,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfilePerfFreqOffset);
        Assert.AreEqual(
            IntPtr.Size == 8 ? 396 : 380,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfileBuffersLostOffset);
    }

    [TestMethod]
    public void RealtimeOpenBufferStartsZeroedAndWritesOnlyRequiredInputs()
    {
        var logger = new IntPtr(0x1111);
        var bufferCallback = new IntPtr(0x2222);
        var eventCallback = new IntPtr(0x3333);
        var context = new IntPtr(0x4444);
        const uint mode = 0x10000100;

        using var buffer = WindowsDiskIoTraceLogfileBuffer.CreateForRealtimeOpen(
            logger,
            mode,
            bufferCallback,
            eventCallback,
            context);

        Assert.AreEqual(
            IntPtr.Size == 8 ? 448 : 416,
            buffer.TotalSize);
        Assert.AreEqual(IntPtr.Zero, buffer.ReadPointer(0));
        Assert.AreEqual(logger, buffer.ReadPointer(WindowsDiskIoTraceLogfileBuffer.LoggerNameOffset));
        Assert.AreEqual(mode, buffer.ReadUInt32(WindowsDiskIoTraceLogfileBuffer.ProcessTraceModeOffset));
        Assert.AreEqual(bufferCallback, buffer.ReadPointer(WindowsDiskIoTraceLogfileBuffer.BufferCallbackOffset));
        Assert.AreEqual(eventCallback, buffer.ReadPointer(WindowsDiskIoTraceLogfileBuffer.EventRecordCallbackOffset));
        Assert.AreEqual(context, buffer.ReadPointer(WindowsDiskIoTraceLogfileBuffer.ContextOffset));

        var bytes = buffer.SnapshotBytes();
        var currentTimeOffset = IntPtr.Size == 8 ? 16 : 8;
        Assert.IsTrue(bytes.Skip(currentTimeOffset).Take(8).All(value => value == 0));
    }

    [TestMethod]
    public void RealtimeOpenBufferRejectsMissingRequiredPointers()
    {
        var nonzero = new IntPtr(1);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            WindowsDiskIoTraceLogfileBuffer.CreateForRealtimeOpen(
                IntPtr.Zero,
                1,
                nonzero,
                nonzero,
                IntPtr.Zero));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            WindowsDiskIoTraceLogfileBuffer.CreateForRealtimeOpen(
                nonzero,
                1,
                IntPtr.Zero,
                nonzero,
                IntPtr.Zero));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            WindowsDiskIoTraceLogfileBuffer.CreateForRealtimeOpen(
                nonzero,
                1,
                nonzero,
                IntPtr.Zero,
                IntPtr.Zero));
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
        var actual = Assert.ThrowsException<InvalidOperationException>(() =>
            InvokeThrowCallbackFault(api));
        Assert.AreSame(expected, actual);
    }

    [TestMethod]
    public void NullEventRecordFailsClosedWithoutCallingSink()
    {
        var sink = new RecordingSink();
        var api = new WindowsDiskIoNativeTraceConsumerApi(sink);

        InvokeEventCallback(api, IntPtr.Zero);

        Assert.AreEqual(0, sink.EventCalls);
        Assert.AreEqual(0u, InvokeBufferCallback(api, new IntPtr(789)));
        Assert.ThrowsException<InvalidDataException>(() =>
            InvokeThrowCallbackFault(api));
    }

    [TestMethod]
    public void BufferCallbackExceptionReturnsFalseAndPreservesOriginalFault()
    {
        var expected = new IOException("buffer fault");
        var sink = new RecordingSink { BufferException = expected };
        var api = new WindowsDiskIoNativeTraceConsumerApi(sink);

        Assert.AreEqual(0u, InvokeBufferCallback(api, new IntPtr(789)));
        var actual = Assert.ThrowsException<IOException>(() =>
            InvokeThrowCallbackFault(api));
        Assert.AreSame(expected, actual);
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
