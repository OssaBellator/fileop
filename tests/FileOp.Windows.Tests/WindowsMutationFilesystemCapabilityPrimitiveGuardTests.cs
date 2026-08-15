using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsMutationFilesystemCapabilityPrimitiveGuardTests
{
    [TestMethod]
    public async Task RefsSourceBlocksCopyBeforeRawPrimitiveDelegation()
    {
        var request = CreateCopyRequest();
        var inner = new FakeCopyPrimitive();
        var probe = new QueueProbe(
            Capability(
                request.SourceDirectory,
                WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem,
                "ReFS"));
        var guarded = new WindowsNtfsFileCopyMutationPrimitive(inner, probe);

        await AssertThrowsAsync<NotSupportedException>(async () =>
            await guarded.CopyNewFileAsync(request));

        Assert.AreEqual(0, inner.CallCount);
        Assert.AreEqual(1, probe.CallCount);
    }

    [TestMethod]
    public async Task RefsDestinationBlocksCopyBeforeRawPrimitiveDelegation()
    {
        var request = CreateCopyRequest();
        var inner = new FakeCopyPrimitive();
        var probe = new QueueProbe(
            Capability(request.SourceDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"),
            Capability(request.DestinationDirectory, WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem, "ReFS"));
        var guarded = new WindowsNtfsFileCopyMutationPrimitive(inner, probe);

        await AssertThrowsAsync<NotSupportedException>(async () =>
            await guarded.CopyNewFileAsync(request));

        Assert.AreEqual(0, inner.CallCount);
        Assert.AreEqual(2, probe.CallCount);
    }

    [TestMethod]
    public async Task ExactNtfsCopyEvidenceDelegatesToRawPrimitive()
    {
        var request = CreateCopyRequest();
        var inner = new FakeCopyPrimitive();
        var probe = new QueueProbe(
            Capability(request.SourceDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"),
            Capability(request.DestinationDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"));
        var guarded = new WindowsNtfsFileCopyMutationPrimitive(inner, probe);

        var lease = await guarded.CopyNewFileAsync(request);

        Assert.AreEqual(1, inner.CallCount);
        Assert.AreSame(inner.Lease, lease);
        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task RefsRootBlocksSameVolumeRenameBeforeRawPrimitiveDelegation()
    {
        var request = CreateMoveRequest();
        var inner = new FakeMovePrimitive();
        var probe = new QueueProbe(
            Capability(
                request.SourceDirectory,
                WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem,
                "ReFS"));
        var guarded = new WindowsNtfsFileSameVolumeMoveMutationPrimitive(inner, probe);

        await AssertThrowsAsync<NotSupportedException>(async () =>
            await guarded.RenameFileAsync(request));

        Assert.AreEqual(0, inner.CallCount);
        Assert.AreEqual(1, probe.CallCount);
    }

    [TestMethod]
    public async Task ExactNtfsRenameEvidenceDelegatesToRawPrimitive()
    {
        var request = CreateMoveRequest();
        var inner = new FakeMovePrimitive();
        var probe = new QueueProbe(
            Capability(request.SourceDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"),
            Capability(request.DestinationDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"));
        var guarded = new WindowsNtfsFileSameVolumeMoveMutationPrimitive(inner, probe);

        var lease = await guarded.RenameFileAsync(request);

        Assert.AreEqual(1, inner.CallCount);
        Assert.AreSame(inner.Lease, lease);
        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task RefsRootBlocksFinalDeleteLeaseBeforeRawProviderDelegation()
    {
        var request = CreateDeleteRequest();
        var inner = new FakeFinalDeleteProvider();
        var probe = new QueueProbe(new WindowsMutationFilesystemCapability(
            request.Authorization.CanonicalSourceDirectoryPath,
            request.Authorization.SourceDirectoryIdentity,
            WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem,
            "ReFS",
            "filesystem is ReFS"));
        var guarded = new WindowsNtfsFileDeleteOperationFinalMutationLeaseProvider(inner, probe);

        await AssertThrowsAsync<NotSupportedException>(async () =>
            await guarded.AcquireAsync(request));

        Assert.AreEqual(0, inner.CallCount);
        Assert.AreEqual(1, probe.CallCount);
    }

    [TestMethod]
    public async Task ExactNtfsDeleteEvidenceDelegatesToRawFinalLeaseProvider()
    {
        var request = CreateDeleteRequest();
        var inner = new FakeFinalDeleteProvider();
        var probe = new QueueProbe(new WindowsMutationFilesystemCapability(
            request.Authorization.CanonicalSourceDirectoryPath,
            request.Authorization.SourceDirectoryIdentity,
            WindowsMutationFilesystemCapabilityState.SupportedNtfs,
            "NTFS",
            "filesystem is NTFS"));
        var guarded = new WindowsNtfsFileDeleteOperationFinalMutationLeaseProvider(inner, probe);

        var lease = await guarded.AcquireAsync(request);

        Assert.AreEqual(1, inner.CallCount);
        Assert.AreSame(inner.Lease, lease);
        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task ProtectedLocationPolicyConstructorPreservesRawDeletePolicy()
    {
        var request = CreateDeleteRequest();
        var probe = new QueueProbe(new WindowsMutationFilesystemCapability(
            request.Authorization.CanonicalSourceDirectoryPath,
            request.Authorization.SourceDirectoryIdentity,
            WindowsMutationFilesystemCapabilityState.SupportedNtfs,
            "NTFS",
            "filesystem is NTFS"));
        var guarded = new WindowsNtfsFileDeleteOperationFinalMutationLeaseProvider(
            new AlwaysBlockedPolicy(),
            probe);

        await AssertThrowsAsync<UnauthorizedAccessException>(async () =>
            await guarded.AcquireAsync(request));

        Assert.AreEqual(1, probe.CallCount);
    }


    [TestMethod]
    public async Task MisboundSupportedEvidenceDoesNotReachAnyRawMutationProvider()
    {
        var request = CreateCopyRequest();
        var inner = new FakeCopyPrimitive();
        var sourceIdentity = request.SourceDirectory.Identity!.Value;
        var probe = new QueueProbe(new WindowsMutationFilesystemCapability(
            request.SourceDirectory.CanonicalPath,
            new FileIdentity(sourceIdentity.VolumeSerialNumber, sourceIdentity.FileReferenceNumber + 1),
            WindowsMutationFilesystemCapabilityState.SupportedNtfs,
            "NTFS",
            "wrong identity"));
        var guarded = new WindowsNtfsFileCopyMutationPrimitive(inner, probe);

        await AssertThrowsAsync<NotSupportedException>(async () =>
            await guarded.CopyNewFileAsync(request));

        Assert.AreEqual(0, inner.CallCount);
    }

    private static FileCopyMutationRequest CreateCopyRequest()
    {
        var roots = CreateRoots(differentVolumes: true);
        return new FileCopyMutationRequest(
            CreateReadyItem(roots.Source, roots.Destination),
            roots.Source,
            roots.Destination);
    }

    private static FileSameVolumeMoveMutationRequest CreateMoveRequest()
    {
        var roots = CreateRoots(differentVolumes: false);
        return new FileSameVolumeMoveMutationRequest(
            CreateReadyItem(roots.Source, roots.Destination),
            roots.Source,
            roots.Destination);
    }

    private static (FileOperationCanonicalPath Source, FileOperationCanonicalPath Destination) CreateRoots(
        bool differentVolumes)
    {
        var source = new FileOperationCanonicalPath(
            @"C:\Source",
            @"C:\Source",
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(1, 10));
        var destination = new FileOperationCanonicalPath(
            differentVolumes ? @"D:\Destination" : @"C:\Destination",
            differentVolumes ? @"D:\Destination" : @"C:\Destination",
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(differentVolumes ? 2u : 1u, 20));
        return (source, destination);
    }

    private static FileOperationExecutionValidationItem CreateReadyItem(
        FileOperationCanonicalPath sourceRoot,
        FileOperationCanonicalPath destinationRoot)
    {
        var entry = new FileOperationEntry(
            Path.Combine(sourceRoot.CanonicalPath, "a.txt"),
            "a.txt",
            false);
        return new FileOperationExecutionValidationItem(
            entry,
            new FileOperationCanonicalPath(
                entry.Path,
                entry.Path,
                FileOperationCanonicalPathState.File,
                false,
                new FileIdentity(sourceRoot.Identity!.Value.VolumeSerialNumber, 100)),
            new FileOperationCanonicalPath(
                Path.Combine(destinationRoot.CanonicalPath, entry.Name),
                Path.Combine(destinationRoot.CanonicalPath, entry.Name),
                FileOperationCanonicalPathState.Missing,
                false),
            FileOperationExecutionValidationDecision.Ready,
            "ready");
    }

    private static WindowsMutationFilesystemCapability Capability(
        FileOperationCanonicalPath root,
        WindowsMutationFilesystemCapabilityState state,
        string? fileSystemName) =>
        new(
            root.CanonicalPath,
            root.Identity!.Value,
            state,
            fileSystemName,
            fileSystemName is null ? "unavailable" : $"filesystem is {fileSystemName}");

    private static FileDeleteOperationFinalMutationLeaseRequest CreateDeleteRequest()
    {
        var path = $@"C:\Users\Alice\Temp\guard-{Guid.NewGuid():N}.tmp";
        var entry = new FileOperationEntry(path, Path.GetFileName(path), false);
        var intent = new FileDeleteOperationIntent(
            "left",
            Guid.NewGuid(),
            @"C:\Users\Alice\Temp",
            new[] { entry });
        var plan = new FileDeleteOperationPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, intent);
        var validation = new FileDeleteOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                intent.SourceDirectoryPath,
                intent.SourceDirectoryPath,
                FileOperationCanonicalPathState.Directory,
                false,
                new FileIdentity(33, 3300)),
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        path,
                        path,
                        FileOperationCanonicalPathState.File,
                        false,
                        new FileIdentity(33, 3301)),
                    FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                    "ready"),
            },
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
        var authorization = new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);

        var constructor = typeof(FileDeleteOperationFinalMutationLeaseRequest).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(FileDeleteOperationUserAuthorizationReceipt), typeof(int) },
            modifiers: null)
            ?? throw new AssertFailedException(
                "Coordinator-only final-delete request constructor was not found.");
        return (FileDeleteOperationFinalMutationLeaseRequest)constructor.Invoke(
            new object[] { authorization, 0 });
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }

    private sealed class AlwaysBlockedPolicy : IFileDeleteProtectedLocationPolicy
    {
        public FileDeleteProtectedLocationResult Evaluate(string canonicalPath) =>
            new(
                FileDeleteProtectedLocationDecision.Blocked,
                "blocked by the test policy");
    }
    private sealed class QueueProbe : IWindowsMutationFilesystemCapabilityProbe
    {
        private readonly Queue<WindowsMutationFilesystemCapability> _results;

        public QueueProbe(params WindowsMutationFilesystemCapability[] results) =>
            _results = new Queue<WindowsMutationFilesystemCapability>(results);

        public int CallCount { get; private set; }

        public WindowsMutationFilesystemCapability QueryDirectory(
            string canonicalDirectoryPath,
            FileIdentity expectedIdentity)
        {
            CallCount++;
            if (_results.Count == 0)
            {
                throw new AssertFailedException("Unexpected filesystem capability query.");
            }
            return _results.Dequeue();
        }
    }

    private sealed class FakeCopyPrimitive : IFileCopyMutationPrimitive
    {
        public FakeCopyPrimitive() => Lease = new FakeCopyLease();

        public int CallCount { get; private set; }

        public FakeCopyLease Lease { get; }

        public ValueTask<IFileCopyMutationLease> CopyNewFileAsync(FileCopyMutationRequest request)
        {
            CallCount++;
            return ValueTask.FromResult<IFileCopyMutationLease>(Lease);
        }
    }

    private sealed class FakeCopyLease : IFileCopyMutationLease
    {
        public FileCopyMutationReceipt Receipt { get; } = new(
            @"C:\Source\a.txt",
            @"D:\Destination\a.txt",
            new FileIdentity(1, 100),
            new FileIdentity(2, 200));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMovePrimitive : IFileSameVolumeMoveMutationPrimitive
    {
        public FakeMovePrimitive() => Lease = new FakeMoveLease();

        public int CallCount { get; private set; }

        public FakeMoveLease Lease { get; }

        public ValueTask<IFileSameVolumeMoveMutationLease> RenameFileAsync(
            FileSameVolumeMoveMutationRequest request)
        {
            CallCount++;
            return ValueTask.FromResult<IFileSameVolumeMoveMutationLease>(Lease);
        }
    }

    private sealed class FakeMoveLease : IFileSameVolumeMoveMutationLease
    {
        public FileSameVolumeMoveMutationReceipt Receipt { get; } = new(
            @"C:\Source\a.txt",
            @"C:\Destination\a.txt",
            new FileIdentity(1, 100),
            new FileIdentity(1, 100));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFinalDeleteProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        public int CallCount { get; private set; }

        public FakeFinalDeleteLease? Lease { get; private set; }

        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            var item = request.AuthorizedItem;
            Lease = new FakeFinalDeleteLease(new FileDeleteOperationFinalMutationLeaseEvidence(
                request,
                request.Authorization.CanonicalSourceDirectoryPath,
                request.Authorization.SourceDirectoryIdentity,
                item.CanonicalPath,
                item.Identity));
            return ValueTask.FromResult<IFileDeleteOperationFinalMutationLease>(Lease);
        }
    }

    private sealed class FakeFinalDeleteLease : IFileDeleteOperationFinalMutationLease
    {
        public FakeFinalDeleteLease(FileDeleteOperationFinalMutationLeaseEvidence evidence) =>
            Evidence = evidence;

        public FileDeleteOperationFinalMutationLeaseEvidence Evidence { get; }

        public bool DeleteAccessCapabilityHeld => true;

        public bool DeleteMutationAuthorized => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
