using System.ComponentModel;
using System.Runtime.InteropServices;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoTraceSessionControllerTests
{
    [TestMethod]
    public void StartPropertiesMatchDocumentedNativeLayoutAndPolicy()
    {
        using var buffer = WindowsDiskIoTracePropertiesBuffer.CreateForStart();
        var properties = buffer.Snapshot;

        Assert.AreEqual(48, Marshal.SizeOf<WindowsDiskIoTracePropertiesBuffer.WnodeHeaderNative>());
        Assert.AreEqual(
            IntPtr.Size == 8 ? 120 : 116,
            Marshal.SizeOf<WindowsDiskIoTracePropertiesBuffer.EventTracePropertiesNative>());
        Assert.AreEqual((uint)buffer.TotalSize, properties.Wnode.BufferSize);
        Assert.AreEqual(
            (WindowsDiskIoSystemSessionPolicy.SessionName.Length + 1) * sizeof(char),
            buffer.LoggerNameCapacityBytes);
        Assert.AreEqual(WindowsDiskIoSystemSessionPolicy.SessionGuid, properties.Wnode.Guid);
        Assert.AreEqual(WindowsDiskIoSystemSessionPolicy.QueryPerformanceCounterClock, properties.Wnode.ClientContext);
        Assert.AreEqual(WindowsDiskIoTracePropertiesBuffer.WnodeFlagTracedGuid, properties.Wnode.Flags);
        Assert.AreEqual(16u, properties.BufferSize);
        Assert.AreEqual(0u, properties.MinimumBuffers);
        Assert.AreEqual(64u, properties.MaximumBuffers);
        Assert.AreEqual(WindowsDiskIoSystemSessionPolicy.LogFileMode, properties.LogFileMode);
        Assert.AreEqual(WindowsDiskIoSystemSessionPolicy.EnableFlags, properties.EnableFlags);
        Assert.AreEqual(0u, properties.LogFileNameOffset);
        Assert.AreEqual((uint)buffer.StructureSize, properties.LoggerNameOffset);
        Assert.AreEqual(string.Empty, buffer.ReadLoggerName());
    }

    [TestMethod]
    public void StopPropertiesContainOnlyControlIdentityAndOffsets()
    {
        using var buffer = WindowsDiskIoTracePropertiesBuffer.CreateForStop();
        var properties = buffer.Snapshot;

        Assert.AreEqual((uint)buffer.TotalSize, properties.Wnode.BufferSize);
        Assert.AreEqual(WindowsDiskIoSystemSessionPolicy.SessionGuid, properties.Wnode.Guid);
        Assert.AreEqual(WindowsDiskIoTracePropertiesBuffer.WnodeFlagTracedGuid, properties.Wnode.Flags);
        Assert.AreEqual(0u, properties.Wnode.ClientContext);
        Assert.AreEqual(0u, properties.BufferSize);
        Assert.AreEqual(0u, properties.LogFileMode);
        Assert.AreEqual(0u, properties.EnableFlags);
        Assert.AreEqual(0u, properties.LogFileNameOffset);
        Assert.AreEqual((uint)buffer.StructureSize, properties.LoggerNameOffset);
        Assert.AreEqual(string.Empty, buffer.ReadLoggerName());
    }

    [TestMethod]
    public void SuccessfulStartOwnsExactTraceIdAndStopsOnce()
    {
        var api = new FakeTraceControlApi
        {
            StartStatus = WindowsDiskIoSystemSessionPolicy.ErrorSuccess,
            StartedTraceId = 0x1122334455667788,
            StopStatus = WindowsDiskIoSystemSessionPolicy.ErrorSuccess,
        };
        var controller = new WindowsDiskIoTraceSessionController(api);

        var start = controller.Start();
        Assert.IsTrue(start.Started);
        Assert.IsNull(start.Failure);
        Assert.IsNotNull(start.Session);
        Assert.AreEqual(1, api.StartCalls);
        Assert.AreEqual(WindowsDiskIoSystemSessionPolicy.SessionName, api.StartInstanceName);
        Assert.AreEqual(0x1122334455667788UL, start.Session.TraceId);

        Assert.AreEqual(
            WindowsDiskIoSessionStopDisposition.Stopped,
            start.Session.Stop());
        Assert.AreEqual(1, api.StopCalls);
        Assert.AreEqual(0x1122334455667788UL, api.StoppedTraceId);
        Assert.AreEqual(0u, api.StopProperties.LogFileNameOffset);
        Assert.AreEqual(WindowsDiskIoSystemSessionPolicy.SessionGuid, api.StopProperties.Wnode.Guid);

        Assert.AreEqual(
            WindowsDiskIoSessionStopDisposition.Stopped,
            start.Session.Stop());
        start.Session.Dispose();
        Assert.AreEqual(1, api.StopCalls);
    }

    [TestMethod]
    public void ExpectedStartFailureNeverCreatesOrStopsSession()
    {
        var api = new FakeTraceControlApi
        {
            StartStatus = WindowsDiskIoSystemSessionPolicy.ErrorAlreadyExists,
            StartedTraceId = 99,
        };
        var controller = new WindowsDiskIoTraceSessionController(api);

        var result = controller.Start();

        Assert.IsFalse(result.Started);
        Assert.IsNull(result.Session);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(DiskIoCaptureStatus.SessionUnavailable, result.Failure.Status);
        Assert.AreEqual(1, api.StartCalls);
        Assert.AreEqual(0, api.StopCalls);
    }

    [TestMethod]
    public void AccessDeniedReturnsPermissionRequiredWithoutElevationOrTakeover()
    {
        var api = new FakeTraceControlApi
        {
            StartStatus = WindowsDiskIoSystemSessionPolicy.ErrorAccessDenied,
        };

        var result = new WindowsDiskIoTraceSessionController(api).Start();

        Assert.IsFalse(result.Started);
        Assert.AreEqual(DiskIoCaptureStatus.PermissionRequired, result.Failure!.Status);
        Assert.AreEqual(0, api.StopCalls);
    }

    [TestMethod]
    public void UnexpectedStartFailureRemainsNativeErrorAndNeverStops()
    {
        var api = new FakeTraceControlApi
        {
            StartStatus = 87,
            StartedTraceId = 0,
        };

        var exception = Assert.ThrowsException<Win32Exception>(() =>
            new WindowsDiskIoTraceSessionController(api).Start());

        Assert.AreEqual(87, exception.NativeErrorCode);
        Assert.AreEqual(0, api.StopCalls);
    }

    [TestMethod]
    public void SuccessfulStartWithZeroTraceIdFailsClosed()
    {
        var api = new FakeTraceControlApi
        {
            StartStatus = WindowsDiskIoSystemSessionPolicy.ErrorSuccess,
            StartedTraceId = 0,
        };

        Assert.ThrowsException<InvalidDataException>(() =>
            new WindowsDiskIoTraceSessionController(api).Start());
        Assert.AreEqual(0, api.StopCalls);
    }

    [TestMethod]
    public void StopDispositionsAreTerminalAndIdempotent()
    {
        foreach (var (status, expected) in new[]
        {
            (WindowsDiskIoSystemSessionPolicy.ErrorMoreData, WindowsDiskIoSessionStopDisposition.StoppedWithTruncatedStatistics),
            (WindowsDiskIoSystemSessionPolicy.ErrorWmiInstanceNotFound, WindowsDiskIoSessionStopDisposition.AlreadyStopped),
            (WindowsDiskIoSystemSessionPolicy.ErrorActiveConnections, WindowsDiskIoSessionStopDisposition.StopInProgress),
        })
        {
            var api = new FakeTraceControlApi
            {
                StartStatus = WindowsDiskIoSystemSessionPolicy.ErrorSuccess,
                StartedTraceId = 101,
                StopStatus = status,
            };
            using var session = new WindowsDiskIoTraceSessionController(api).Start().Session!;

            Assert.AreEqual(expected, session.Stop());
            Assert.AreEqual(expected, session.Stop());
            Assert.AreEqual(1, api.StopCalls);
        }
    }

    [TestMethod]
    public void UnexpectedStopFailureIsTerminalAndNeverRetried()
    {
        var api = new FakeTraceControlApi
        {
            StartStatus = WindowsDiskIoSystemSessionPolicy.ErrorSuccess,
            StartedTraceId = 202,
            StopStatus = WindowsDiskIoSystemSessionPolicy.ErrorAccessDenied,
        };
        var session = new WindowsDiskIoTraceSessionController(api).Start().Session!;

        var first = Assert.ThrowsException<Win32Exception>(() => session.Stop());
        Assert.AreEqual((int)WindowsDiskIoSystemSessionPolicy.ErrorAccessDenied, first.NativeErrorCode);
        Assert.AreEqual(1, api.StopCalls);
        Assert.IsTrue(session.StopAttempted);

        Assert.ThrowsException<InvalidOperationException>(() => session.Stop());
        session.Dispose();
        Assert.AreEqual(1, api.StopCalls);
    }

    [TestMethod]
    public void DisposeStopsActiveOwnedSession()
    {
        var api = new FakeTraceControlApi
        {
            StartStatus = WindowsDiskIoSystemSessionPolicy.ErrorSuccess,
            StartedTraceId = 303,
            StopStatus = WindowsDiskIoSystemSessionPolicy.ErrorSuccess,
        };
        var session = new WindowsDiskIoTraceSessionController(api).Start().Session!;

        session.Dispose();
        session.Dispose();

        Assert.AreEqual(1, api.StopCalls);
        Assert.IsTrue(session.StopAttempted);
    }

    private sealed class FakeTraceControlApi : IWindowsDiskIoTraceControlApi
    {
        public uint StartStatus { get; init; }
        public ulong StartedTraceId { get; init; }
        public uint StopStatus { get; init; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public string? StartInstanceName { get; private set; }
        public ulong StoppedTraceId { get; private set; }
        public WindowsDiskIoTracePropertiesBuffer.EventTracePropertiesNative StartProperties { get; private set; }
        public WindowsDiskIoTracePropertiesBuffer.EventTracePropertiesNative StopProperties { get; private set; }

        public uint StartTrace(
            out ulong traceId,
            string instanceName,
            IntPtr properties)
        {
            StartCalls++;
            StartInstanceName = instanceName;
            StartProperties = Marshal.PtrToStructure<WindowsDiskIoTracePropertiesBuffer.EventTracePropertiesNative>(properties);
            traceId = StartedTraceId;
            return StartStatus;
        }

        public uint StopTrace(ulong traceId, IntPtr properties)
        {
            StopCalls++;
            StoppedTraceId = traceId;
            StopProperties = Marshal.PtrToStructure<WindowsDiskIoTracePropertiesBuffer.EventTracePropertiesNative>(properties);
            return StopStatus;
        }
    }
}
