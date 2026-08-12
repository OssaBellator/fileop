using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileDeleteOperationFinalMutationLeaseProviderTests
{
    [TestMethod]
    public async Task FinalLeaseBindsExactAuthorizationAndHoldsDeleteCapabilityWithoutMutationAuthority()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "authorized-content");
        var authorization = await fixture.AuthorizeAsync();
        var provider = fixture.CreateFinalProvider();

        var scope = await fixture.AcquireFinalScopeAsync(authorization, provider);
        try
        {
            Assert.AreSame(authorization, scope.Authorization);
            Assert.IsTrue(scope.FinalEvidence.IsBoundTo(authorization, 0));
            Assert.AreEqual(
                authorization.CanonicalSourceDirectoryPath,
                scope.FinalEvidence.CanonicalSourceDirectoryPath);
            Assert.AreEqual(
                authorization.SourceDirectoryIdentity,
                scope.FinalEvidence.SourceDirectoryIdentity);
            Assert.AreEqual(
                authorization.Items[0].CanonicalPath,
                scope.FinalEvidence.CanonicalSourcePath);
            Assert.AreEqual(
                authorization.Items[0].Identity,
                scope.FinalEvidence.SourceIdentity);
            Assert.IsTrue(scope.FinalLeaseHeld);
            Assert.IsTrue(scope.DeleteAccessCapabilityHeld);
            Assert.IsFalse(scope.DeleteMutationAuthorized);
            Assert.IsFalse(scope.MutationBarrierSatisfied);
            Assert.IsFalse(scope.DeleteMutationPerformed);
            Assert.IsFalse(scope.FinalEvidence.ProviderAcquisitionProven);
            Assert.IsFalse(scope.FinalEvidence.DeleteAccessCapabilityProven);
            Assert.IsFalse(scope.FinalEvidence.LeaseLivenessProven);

            Assert.IsTrue(DeleteIsBlocked(fixture.SourcePath));
            Assert.IsTrue(WriteOpenIsBlocked(fixture.SourcePath));
            Assert.IsTrue(DirectoryRenameIsBlocked(
                fixture.RootPath,
                fixture.RootPath + ".moved"));
            Assert.IsTrue(File.Exists(fixture.SourcePath));
        }
        finally
        {
            await scope.DisposeAsync();
        }

        Assert.IsFalse(scope.FinalLeaseHeld);
        Assert.IsFalse(scope.DeleteAccessCapabilityHeld);
        Assert.AreEqual(
            "authorized-content",
            await File.ReadAllTextAsync(fixture.SourcePath));
        await File.AppendAllTextAsync(fixture.SourcePath, "-after-dispose");
        Assert.AreEqual(
            "authorized-content-after-dispose",
            await File.ReadAllTextAsync(fixture.SourcePath));
        File.Delete(fixture.SourcePath);
        Assert.IsFalse(File.Exists(fixture.SourcePath));
    }

    [TestMethod]
    public async Task FileReplacementDuringReadOnlyToFinalHandoffIsRejectedByFinalProvider()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        var authorization = await fixture.AuthorizeAsync();
        var originalPath = fixture.SourcePath + ".original";
        var provider = new BeforeAcquireProvider(
            fixture.CreateFinalProvider(),
            () =>
            {
                File.Move(fixture.SourcePath, originalPath);
                File.WriteAllText(fixture.SourcePath, "replacement");
            });

        await AssertThrowsAsync<IOException>(async () =>
            await fixture.AcquireFinalScopeAsync(authorization, provider));

        Assert.AreEqual(1, provider.AcquireCount);
        Assert.AreEqual("original", await File.ReadAllTextAsync(originalPath));
        Assert.AreEqual("replacement", await File.ReadAllTextAsync(fixture.SourcePath));
    }

    [TestMethod]
    public async Task RootReplacementDuringReadOnlyToFinalHandoffIsRejectedByFinalProvider()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        var authorization = await fixture.AuthorizeAsync();
        var originalRoot = fixture.RootPath + ".original";
        var provider = new BeforeAcquireProvider(
            fixture.CreateFinalProvider(),
            () =>
            {
                Directory.Move(fixture.RootPath, originalRoot);
                Directory.CreateDirectory(fixture.RootPath);
                File.WriteAllText(fixture.SourcePath, "replacement");
            });

        try
        {
            await AssertThrowsAsync<IOException>(async () =>
                await fixture.AcquireFinalScopeAsync(authorization, provider));

            Assert.AreEqual(1, provider.AcquireCount);
            Assert.AreEqual(
                "original",
                await File.ReadAllTextAsync(
                    Path.Combine(originalRoot, DeleteFixture.FileName)));
            Assert.AreEqual(
                "replacement",
                await File.ReadAllTextAsync(fixture.SourcePath));
        }
        finally
        {
            try
            {
                Directory.Delete(originalRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [TestMethod]
    public async Task ProtectedLocationPolicyIsRecheckedByFinalProviderAfterReadOnlyRelease()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "authorized-content");
        var authorization = await fixture.AuthorizeAsync();
        var provider = new WindowsFileDeleteOperationFinalMutationLeaseProvider(
            new AlwaysBlockedPolicy());

        await AssertThrowsAsync<UnauthorizedAccessException>(async () =>
            await fixture.AcquireFinalScopeAsync(authorization, provider));

        Assert.IsTrue(File.Exists(fixture.SourcePath));
        Assert.AreEqual(
            "authorized-content",
            await File.ReadAllTextAsync(fixture.SourcePath));
    }

    [TestMethod]
    public async Task ConcreteProviderHonorsPreCancellationBeforeNativeAcquisition()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "authorized-content");
        var authorization = await fixture.AuthorizeAsync();
        var concrete = fixture.CreateFinalProvider();
        var capturing = new CapturingProvider(concrete);
        var scope = await fixture.AcquireFinalScopeAsync(authorization, capturing);
        var request = capturing.LastRequest!;
        await scope.DisposeAsync();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await concrete.AcquireAsync(request, cancellation.Token));

        Assert.IsTrue(File.Exists(fixture.SourcePath));
        Assert.AreEqual(
            "authorized-content",
            await File.ReadAllTextAsync(fixture.SourcePath));
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

    private static bool DeleteIsBlocked(string path)
    {
        if (DeleteFileW(path))
        {
            return false;
        }

        Assert.AreEqual(
            32, // ERROR_SHARING_VIOLATION.
            Marshal.GetLastPInvokeError(),
            "The final delete-capability lease should block an independent native delete while it is held.");
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string lpFileName);

    private static bool WriteOpenIsBlocked(string path)
    {
        try
        {
            using var writer = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool DirectoryRenameIsBlocked(string path, string destination)
    {
        try
        {
            Directory.Move(path, destination);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class AlwaysBlockedPolicy : IFileDeleteProtectedLocationPolicy
    {
        public FileDeleteProtectedLocationResult Evaluate(string canonicalPath) =>
            new(
                FileDeleteProtectedLocationDecision.Blocked,
                "blocked by final-provider test policy");
    }

    private sealed class CapturingProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        private readonly IFileDeleteOperationFinalMutationLeaseProvider _inner;

        public CapturingProvider(IFileDeleteOperationFinalMutationLeaseProvider inner)
        {
            _inner = inner;
        }

        public FileDeleteOperationFinalMutationLeaseRequest? LastRequest { get; private set; }

        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return _inner.AcquireAsync(request, cancellationToken);
        }
    }

    private sealed class BeforeAcquireProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        private readonly IFileDeleteOperationFinalMutationLeaseProvider _inner;
        private readonly Action _beforeAcquire;

        public BeforeAcquireProvider(
            IFileDeleteOperationFinalMutationLeaseProvider inner,
            Action beforeAcquire)
        {
            _inner = inner;
            _beforeAcquire = beforeAcquire;
        }

        public int AcquireCount { get; private set; }

        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            _beforeAcquire();
            return _inner.AcquireAsync(request, cancellationToken);
        }
    }

    private sealed class FakeHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        private readonly FileDeleteOperationActionHistory _history;

        public FakeHistoryStore(FileDeleteOperationActionHistory history)
        {
            _history = history;
        }

        public ValueTask<FileDeleteOperationActionHistory?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<FileDeleteOperationActionHistory?>(_history);
        }

        public ValueTask<FileDeleteOperationActionHistory> BeginAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
            Guid operationId,
            int ordinal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(
            Guid operationId,
            int ordinal,
            FileIdentity deletedSourceIdentity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> MarkFailedAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationRecoveryRequiredAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> CompleteAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DeleteFixture : IDisposable
    {
        public const string FileName = "payload.tmp";

        private static readonly IFileDeleteProtectedLocationPolicy PermissivePolicy =
            new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>());

        public DeleteFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.FinalDeleteLease.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            SourcePath = Path.Combine(RootPath, FileName);
        }

        public string RootPath { get; }

        public string SourcePath { get; }

        public WindowsFileDeleteOperationFinalMutationLeaseProvider CreateFinalProvider() =>
            new(PermissivePolicy);

        public async Task<FileDeleteOperationUserAuthorizationReceipt> AuthorizeAsync()
        {
            var entry = new FileOperationEntry(SourcePath, FileName, IsDirectory: false);
            var plan = new FileDeleteOperationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new FileDeleteOperationIntent(
                    "left",
                    Guid.NewGuid(),
                    RootPath,
                    new[] { entry }));
            var validation = await new WindowsFileDeleteOperationExecutionValidator(
                    protectedLocationPolicy: PermissivePolicy)
                .ValidateAsync(plan);
            Assert.AreEqual(
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                validation.Status);
            Assert.IsTrue(validation.CanRequestAuthorizationReview);
            Assert.IsFalse(validation.DeleteMutationAuthorized);

            return new FileDeleteOperationUserAuthorizationIssuer()
                .IssueAfterExplicitUserConfirmation(validation);
        }

        public async Task<FileDeleteOperationFinalMutationLeaseScope> AcquireFinalScopeAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            IFileDeleteOperationFinalMutationLeaseProvider finalProvider)
        {
            await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
            var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                new WindowsFileDeleteOperationStabilityLeaseProvider(PermissivePolicy),
                historyStore);
            return await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
                preparation,
                finalProvider);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static FileDeleteOperationActionHistory CreateHistory(
            FileDeleteOperationUserAuthorizationReceipt authorization)
        {
            var item = authorization.Items[0];
            return new FileDeleteOperationActionHistory(
                authorization.PlanId,
                authorization.AuthorizationId,
                authorization.Plan.QueuedAtUtc,
                authorization.ValidatedAtUtc,
                authorization.AuthorizedAtUtc,
                authorization.AuthorizedAtUtc.AddMilliseconds(1),
                completedAtUtc: null,
                authorization.Plan.Intent.SourcePane,
                authorization.Plan.Intent.SourceTabId,
                authorization.CanonicalSourceDirectoryPath,
                authorization.SourceDirectoryIdentity,
                terminalState: null,
                new[]
                {
                    new FileDeleteOperationActionEntry(
                        0,
                        item.Entry,
                        item.CanonicalPath,
                        item.Identity,
                        FileDeleteOperationActionEntryState.Pending,
                        MutationStartedAtUtc: null,
                        CompletedAtUtc: null,
                        Failure: null),
                });
        }
    }
}
