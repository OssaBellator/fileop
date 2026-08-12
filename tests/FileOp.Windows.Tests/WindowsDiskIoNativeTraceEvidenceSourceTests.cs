using System.Reflection;
using System.Runtime.InteropServices;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoNativeTraceEvidenceSourceTests
{
    [TestMethod]
    public void StableOwnedStateReturnsTraceEvidence()
    {
        using var fixture = NativeStateFixture.Create();
        Marshal.WriteInt64(
            fixture.Logfile.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfilePerfFreqOffset,
            10_000_000);
        Marshal.WriteInt32(
            fixture.Logfile.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfileEventsLostOffset,
            8);
        Marshal.WriteInt32(
            fixture.Logfile.Pointer,
            WindowsDiskIoTraceLogfileBuffer.TraceLogfileBuffersLostOffset,
            2);

        var evidence = fixture.Api.ReadTraceEvidence(fixture.Handle);

        Assert.AreEqual(10_000_000L, evidence.PerformanceCounterFrequency);
        Assert.AreEqual(8u, evidence.EventsLost);
        Assert.AreEqual(2u, evidence.BuffersLost);
    }

    [TestMethod]
    public void ActiveProcessTraceBlocksEvidenceRead()
    {
        using var fixture = NativeStateFixture.Create();
        SetField(fixture.Api, "_processActive", true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Api.ReadTraceEvidence(fixture.Handle));

        StringAssert.Contains(exception.Message, "ProcessTrace");
    }

    [TestMethod]
    public void InFlightOpenBlocksEvidenceRead()
    {
        using var fixture = NativeStateFixture.Create();
        SetField(fixture.Api, "_openActive", true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Api.ReadTraceEvidence(fixture.Handle));

        StringAssert.Contains(exception.Message, "OpenTraceW");
    }

    [TestMethod]
    public void WrongOrReleasedHandleCannotReadEvidence()
    {
        using var fixture = NativeStateFixture.Create();

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Api.ReadTraceEvidence(fixture.Handle + 1));

        SetField(fixture.Api, "_openedHandle", 0UL);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Api.ReadTraceEvidence(fixture.Handle));
    }

    private static void SetField<T>(
        WindowsDiskIoNativeTraceConsumerApi api,
        string name,
        T value) =>
        typeof(WindowsDiskIoNativeTraceConsumerApi)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(api, value);

    private sealed class NativeStateFixture : IDisposable
    {
        private IntPtr _loggerName;

        private NativeStateFixture(
            WindowsDiskIoNativeTraceConsumerApi api,
            WindowsDiskIoTraceLogfileBuffer logfile,
            IntPtr loggerName,
            ulong handle)
        {
            Api = api;
            Logfile = logfile;
            _loggerName = loggerName;
            Handle = handle;
        }

        public WindowsDiskIoNativeTraceConsumerApi Api { get; }

        public WindowsDiskIoTraceLogfileBuffer Logfile { get; }

        public ulong Handle { get; }

        public static NativeStateFixture Create()
        {
            const ulong handle = 0x1234;
            var api = new WindowsDiskIoNativeTraceConsumerApi(new NoOpSink());
            var loggerName = Marshal.StringToHGlobalUni("FileOp evidence source test");
            var logfile = WindowsDiskIoTraceLogfileBuffer.CreateForRealtimeOpen(
                loggerName,
                WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode,
                new IntPtr(1),
                new IntPtr(2),
                IntPtr.Zero);

            SetField(api, "_openedHandle", handle);
            SetField(api, "_loggerNameMemory", loggerName);
            SetField(api, "_logfileBuffer", logfile);
            return new NativeStateFixture(api, logfile, loggerName, handle);
        }

        public void Dispose()
        {
            SetField(Api, "_openedHandle", 0UL);
            SetField(Api, "_logfileBuffer", null as WindowsDiskIoTraceLogfileBuffer);
            SetField(Api, "_loggerNameMemory", IntPtr.Zero);
            Logfile.Dispose();
            var loggerName = Interlocked.Exchange(ref _loggerName, IntPtr.Zero);
            if (loggerName != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(loggerName);
            }
        }
    }

    private sealed class NoOpSink : IWindowsDiskIoNativeTraceCallbackSink
    {
        public bool OnEventRecord(IntPtr eventRecord) => true;

        public bool OnBuffer(IntPtr logfile) => true;
    }
}
