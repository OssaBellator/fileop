using System.Reflection;
using System.Runtime.InteropServices;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoNativeTraceConsumerLifecycleGuardsTests
{
    [TestMethod]
    public void ConcurrentOpenIsRejectedWhileOpenTraceIsInFlight()
    {
        var api = new WindowsDiskIoNativeTraceConsumerApi(new NoOpSink());
        typeof(WindowsDiskIoNativeTraceConsumerApi)
            .GetField("_openActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, true);

        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
            api.OpenRealtime(
                WindowsDiskIoSystemSessionPolicy.SessionName,
                WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode));

        StringAssert.Contains(exception.Message, "opening");
    }

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

    [TestMethod]
    public void NativeLogfileStateIsRetainedUntilProcessDrainCompletes()
    {
        var api = new WindowsDiskIoNativeTraceConsumerApi(new NoOpSink());
        var loggerName = Marshal.StringToHGlobalUni("FileOp test logger");
        var logfile = WindowsDiskIoTraceLogfileBuffer.CreateForRealtimeOpen(
            loggerName,
            WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode,
            new IntPtr(1),
            new IntPtr(2),
            IntPtr.Zero);

        var type = typeof(WindowsDiskIoNativeTraceConsumerApi);
        type.GetField("_loggerNameMemory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, loggerName);
        type.GetField("_logfileBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, logfile);
        type.GetField("_processActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, true);

        try
        {
            GetReleaseMethod().Invoke(api, null);
            Assert.AreSame(
                logfile,
                type.GetField("_logfileBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(api));
            Assert.AreEqual(
                loggerName,
                (IntPtr)type.GetField("_loggerNameMemory", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(api)!);

            type.GetField("_processActive", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(api, false);
            GetReleaseMethod().Invoke(api, null);

            Assert.IsNull(
                type.GetField("_logfileBuffer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(api));
            Assert.AreEqual(
                IntPtr.Zero,
                (IntPtr)type.GetField("_loggerNameMemory", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(api)!);

            logfile = null!;
            loggerName = IntPtr.Zero;
        }
        finally
        {
            logfile?.Dispose();
            if (loggerName != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(loggerName);
            }
        }
    }

    private static MethodInfo GetReleaseMethod() =>
        typeof(WindowsDiskIoNativeTraceConsumerApi).GetMethod(
            "ReleaseNativeOpenStateIfSafe",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class NoOpSink : IWindowsDiskIoNativeTraceCallbackSink
    {
        public bool OnEventRecord(IntPtr eventRecord) => true;

        public bool OnBuffer(IntPtr logfile) => true;
    }
}
