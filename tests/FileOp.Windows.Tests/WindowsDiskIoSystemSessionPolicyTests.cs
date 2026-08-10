using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoSystemSessionPolicyTests
{
    [TestMethod]
    public void UsesDedicatedRealtimeSystemLoggerWithoutLegacyKernelIdentity()
    {
        Assert.AreEqual("FileOp Disk I/O Diagnostics", WindowsDiskIoSystemSessionPolicy.SessionName);
        Assert.AreEqual(
            new Guid("6def68d0-e21a-403d-9ba5-0dc373e02eb8"),
            WindowsDiskIoSystemSessionPolicy.SessionGuid);
        Assert.AreEqual(
            WindowsDiskIoSystemSessionPolicy.EventTraceRealTimeMode |
            WindowsDiskIoSystemSessionPolicy.EventTraceSystemLoggerMode,
            WindowsDiskIoSystemSessionPolicy.LogFileMode);
        Assert.AreEqual(
            WindowsDiskIoSystemSessionPolicy.EventTraceFlagDiskIo |
            WindowsDiskIoSystemSessionPolicy.EventTraceFlagNoSysConfig,
            WindowsDiskIoSystemSessionPolicy.EnableFlags);
        Assert.AreEqual(1u, WindowsDiskIoSystemSessionPolicy.QueryPerformanceCounterClock);
        Assert.IsFalse(WindowsDiskIoSystemSessionPolicy.UsesLegacyNtKernelLoggerIdentity);
    }

    [TestMethod]
    public void AccessDeniedMapsOnlyToPermissionRequired()
    {
        Assert.IsTrue(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStartFailure(
            WindowsDiskIoSystemSessionPolicy.ErrorAccessDenied,
            out var failure));
        Assert.IsNotNull(failure);
        Assert.AreEqual(DiskIoCaptureStatus.PermissionRequired, failure.Status);
        Assert.AreEqual(5u, failure.Win32Error);
        StringAssert.Contains(failure.Detail, "will not modify group membership");
    }

    [TestMethod]
    public void ExistingOrCapacityExhaustedSessionMapsToUnavailableWithoutTakeover()
    {
        Assert.IsTrue(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStartFailure(
            WindowsDiskIoSystemSessionPolicy.ErrorAlreadyExists,
            out var existing));
        Assert.IsNotNull(existing);
        Assert.AreEqual(DiskIoCaptureStatus.SessionUnavailable, existing.Status);
        StringAssert.Contains(existing.Detail, "will not stop or reuse");

        Assert.IsTrue(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStartFailure(
            WindowsDiskIoSystemSessionPolicy.ErrorNoSystemResources,
            out var capacity));
        Assert.IsNotNull(capacity);
        Assert.AreEqual(DiskIoCaptureStatus.SessionUnavailable, capacity.Status);
        StringAssert.Contains(capacity.Detail, "will not raise ETW logger limits");
    }

    [TestMethod]
    public void UnexpectedStartErrorsAreNotCollapsedIntoAvailabilityStates()
    {
        Assert.IsFalse(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStartFailure(
            87,
            out var invalidParameter));
        Assert.IsNull(invalidParameter);

        Assert.IsFalse(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStartFailure(
            24,
            out var badLength));
        Assert.IsNull(badLength);
    }

    [TestMethod]
    public void StopResultsDistinguishStoppedMissingAndAlreadyStopping()
    {
        Assert.IsTrue(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStopResult(
            WindowsDiskIoSystemSessionPolicy.ErrorSuccess,
            out var stopped));
        Assert.AreEqual(WindowsDiskIoSessionStopDisposition.Stopped, stopped);

        Assert.IsTrue(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStopResult(
            WindowsDiskIoSystemSessionPolicy.ErrorWmiInstanceNotFound,
            out var missing));
        Assert.AreEqual(WindowsDiskIoSessionStopDisposition.AlreadyStopped, missing);

        Assert.IsTrue(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStopResult(
            WindowsDiskIoSystemSessionPolicy.ErrorActiveConnections,
            out var stopping));
        Assert.AreEqual(WindowsDiskIoSessionStopDisposition.StopInProgress, stopping);
    }

    [TestMethod]
    public void UnexpectedStopErrorsRemainVisibleToProvider()
    {
        Assert.IsFalse(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStopResult(
            WindowsDiskIoSystemSessionPolicy.ErrorAccessDenied,
            out _));
        Assert.IsFalse(WindowsDiskIoSystemSessionPolicy.TryClassifyExpectedStopResult(
            24,
            out _));
    }
}
