using System.ComponentModel;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoTraceConsumerLifecycleTests
{
    [TestMethod]
    public void PolicyUsesRealtimeEventRecordModeWithoutRawTimestamp()
    {
        Assert.AreEqual(
            0x10000100u,
            WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode);
        Assert.IsTrue(
            (WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode &
             WindowsDiskIoTraceConsumerPolicy.ProcessTraceModeRealTime) != 0);
        Assert.IsTrue(
            (WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode &
             WindowsDiskIoTraceConsumerPolicy.ProcessTraceModeEventRecord) != 0);
        Assert.AreEqual(
            IntPtr.Size == 8 ? ulong.MaxValue : uint.MaxValue,
            WindowsDiskIoTraceConsumerPolicy.InvalidProcessTraceHandle);
    }

    [TestMethod]
    public void SuccessfulOpenOwnsExactProcessingHandleAndUsesFixedSessionName()
    {
        var api = new FakeConsumerApi
        {
            OpenResult = new WindowsDiskIoNativeOpenResult(1234, 0),
        };

        var result = new WindowsDiskIoTraceConsumerLifecycle(api).Open();

        Assert.IsTrue(result.Opened);
        Assert.IsNull(result.Failure);
        Assert.IsNotNull(result.Consumer);
        Assert.AreEqual(1234UL, result.Consumer.ProcessingHandle);
        Assert.AreEqual(WindowsDiskIoSystemSessionPolicy.SessionName, api.OpenSessionName);
        Assert.AreEqual(WindowsDiskIoTraceConsumerPolicy.ProcessTraceMode, api.OpenProcessMode);
    }

    [TestMethod]
    public void ZeroOrSentinelProcessingHandleFailsClosed()
    {
        foreach (var invalid in new[]
        {
            0UL,
            WindowsDiskIoTraceConsumerPolicy.InvalidProcessTraceHandle,
        })
        {
            var api = new FakeConsumerApi
            {
                OpenResult = new WindowsDiskIoNativeOpenResult(invalid, 0),
            };

            Assert.ThrowsException<InvalidDataException>(() =>
                new WindowsDiskIoTraceConsumerLifecycle(api).Open());
            Assert.AreEqual(0, api.CloseCalls);
        }
    }

    [TestMethod]
    public void AccessDeniedAndMissingCollectionAreExpectedOpenStates()
    {
        var deniedApi = new FakeConsumerApi
        {
            OpenResult = new WindowsDiskIoNativeOpenResult(0, WindowsDiskIoTraceConsumerPolicy.ErrorAccessDenied),
        };
        var denied = new WindowsDiskIoTraceConsumerLifecycle(deniedApi).Open();
        Assert.IsFalse(denied.Opened);
        Assert.AreEqual(DiskIoCaptureStatus.PermissionRequired, denied.Failure!.Status);
        Assert.AreEqual(0, deniedApi.ProcessCalls);
        Assert.AreEqual(0, deniedApi.CloseCalls);

        var missingApi = new FakeConsumerApi
        {
            OpenResult = new WindowsDiskIoNativeOpenResult(0, WindowsDiskIoTraceConsumerPolicy.ErrorWmiInstanceNotFound),
        };
        var missing = new WindowsDiskIoTraceConsumerLifecycle(missingApi).Open();
        Assert.IsFalse(missing.Opened);
        Assert.AreEqual(DiskIoCaptureStatus.SessionUnavailable, missing.Failure!.Status);
        Assert.AreEqual(0, missingApi.CloseCalls);
    }

    [TestMethod]
    public void UnexpectedOpenFailureRemainsNativeError()
    {
        var api = new FakeConsumerApi
        {
            OpenResult = new WindowsDiskIoNativeOpenResult(0, 87),
        };

        var exception = Assert.ThrowsException<Win32Exception>(() =>
            new WindowsDiskIoTraceConsumerLifecycle(api).Open());

        Assert.AreEqual(87, exception.NativeErrorCode);
        Assert.AreEqual(0, api.CloseCalls);
    }

    [TestMethod]
    public void ProcessResultsPreserveCompletionCancellationAndStoppedCollection()
    {
        foreach (var (status, expected) in new[]
        {
            (WindowsDiskIoTraceConsumerPolicy.ErrorSuccess, WindowsDiskIoTraceProcessDisposition.Completed),
            (WindowsDiskIoTraceConsumerPolicy.ErrorCancelled, WindowsDiskIoTraceProcessDisposition.CancelledByConsumer),
            (WindowsDiskIoTraceConsumerPolicy.ErrorWmiInstanceNotFound, WindowsDiskIoTraceProcessDisposition.CollectionSessionEnded),
        })
        {
            var api = new FakeConsumerApi
            {
                OpenResult = new WindowsDiskIoNativeOpenResult(77, 0),
                ProcessStatus = status,
            };
            using var consumer = new WindowsDiskIoTraceConsumerLifecycle(api).Open().Consumer!;

            Assert.AreEqual(expected, consumer.Process());
            Assert.AreEqual(1, api.ProcessCalls);
            Assert.AreEqual(77UL, api.ProcessedHandle);
        }
    }

    [TestMethod]
    public void CallbackExceptionStatusIsNotMisreportedAsCancellation()
    {
        var api = new FakeConsumerApi
        {
            OpenResult = new WindowsDiskIoNativeOpenResult(88, 0),
            ProcessStatus = WindowsDiskIoTraceConsumerPolicy.ErrorNoAccess,
        };
        using var consumer = new WindowsDiskIoTraceConsumerLifecycle(api).Open().Consumer!;

        var exception = Assert.ThrowsException<Win32Exception>(() => consumer.Process());

        Assert.AreEqual((int)WindowsDiskIoTraceConsumerPolicy.ErrorNoAccess, exception.NativeErrorCode);
    }

    [TestMethod]
    public void CloseSuccessAndPendingAreTerminalIdempotentOutcomes()
    {
        foreach (var (status, expected) in new[]
        {
            (WindowsDiskIoTraceConsumerPolicy.ErrorSuccess, WindowsDiskIoTraceCloseDisposition.Closed),
            (WindowsDiskIoTraceConsumerPolicy.ErrorCtxClosePending, WindowsDiskIoTraceCloseDisposition.ClosePending),
        })
        {
            var api = new FakeConsumerApi
            {
                OpenResult = new WindowsDiskIoNativeOpenResult(99, 0),
                CloseStatus = status,
            };
            using var consumer = new WindowsDiskIoTraceConsumerLifecycle(api).Open().Consumer!;

            Assert.AreEqual(expected, consumer.Close());
            Assert.AreEqual(expected, consumer.Close());
            consumer.Dispose();
            Assert.AreEqual(1, api.CloseCalls);
            Assert.AreEqual(99UL, api.ClosedHandle);
            Assert.ThrowsException<ObjectDisposedException>(() => consumer.Process());
        }
    }

    [TestMethod]
    public void UnexpectedCloseFailureIsTerminalAndNeverRetried()
    {
        var api = new FakeConsumerApi
        {
            OpenResult = new WindowsDiskIoNativeOpenResult(111, 0),
            CloseStatus = WindowsDiskIoTraceConsumerPolicy.ErrorInvalidHandle,
        };
        var consumer = new WindowsDiskIoTraceConsumerLifecycle(api).Open().Consumer!;

        var first = Assert.ThrowsException<Win32Exception>(() => consumer.Close());
        Assert.AreEqual((int)WindowsDiskIoTraceConsumerPolicy.ErrorInvalidHandle, first.NativeErrorCode);
        Assert.AreEqual(1, api.CloseCalls);

        Assert.ThrowsException<InvalidOperationException>(() => consumer.Close());
        consumer.Dispose();
        Assert.AreEqual(1, api.CloseCalls);
        Assert.ThrowsException<InvalidOperationException>(() => consumer.Process());
    }

    [TestMethod]
    public void DisposeClosesActiveConsumerOnce()
    {
        var api = new FakeConsumerApi
        {
            OpenResult = new WindowsDiskIoNativeOpenResult(222, 0),
            CloseStatus = WindowsDiskIoTraceConsumerPolicy.ErrorSuccess,
        };
        var consumer = new WindowsDiskIoTraceConsumerLifecycle(api).Open().Consumer!;

        consumer.Dispose();
        consumer.Dispose();

        Assert.AreEqual(1, api.CloseCalls);
        Assert.AreEqual(222UL, api.ClosedHandle);
    }

    private sealed class FakeConsumerApi : IWindowsDiskIoTraceConsumerApi
    {
        public WindowsDiskIoNativeOpenResult OpenResult { get; init; }
        public uint ProcessStatus { get; init; }
        public uint CloseStatus { get; init; }
        public string? OpenSessionName { get; private set; }
        public uint OpenProcessMode { get; private set; }
        public int ProcessCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public ulong ProcessedHandle { get; private set; }
        public ulong ClosedHandle { get; private set; }

        public WindowsDiskIoNativeOpenResult OpenRealtime(
            string sessionName,
            uint processTraceMode)
        {
            OpenSessionName = sessionName;
            OpenProcessMode = processTraceMode;
            return OpenResult;
        }

        public uint ProcessTrace(ulong processingHandle)
        {
            ProcessCalls++;
            ProcessedHandle = processingHandle;
            return ProcessStatus;
        }

        public uint CloseTrace(ulong processingHandle)
        {
            CloseCalls++;
            ClosedHandle = processingHandle;
            return CloseStatus;
        }
    }
}
