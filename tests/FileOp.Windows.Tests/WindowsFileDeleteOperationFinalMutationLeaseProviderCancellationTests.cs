using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileDeleteOperationFinalMutationLeaseProviderCancellationTests
{
    [TestMethod]
    public async Task CancellationRaisedDuringPolicyEvaluationReturnsNoFinalLeaseOrMutation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FileOp.FinalDeleteLease.Cancellation.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "payload.tmp");
        await File.WriteAllTextAsync(source, "unchanged");
        var permissive = new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>());

        try
        {
            var authorization = await AuthorizeAsync(root, source, permissive);
            var capturing = new CapturingProvider(
                new WindowsFileDeleteOperationFinalMutationLeaseProvider(permissive));
            await using (var historyStore = new FakeHistoryStore(CreateHistory(authorization)))
            {
                var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                    authorization,
                    0,
                    new WindowsFileDeleteOperationStabilityLeaseProvider(permissive),
                    historyStore);
                var initialScope = await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
                    preparation,
                    capturing);
                await initialScope.DisposeAsync();
            }

            Assert.IsNotNull(capturing.LastRequest);
            using var cancellation = new CancellationTokenSource();
            var cancellingPolicy = new CancellingAllowedPolicy(cancellation);
            var provider = new WindowsFileDeleteOperationFinalMutationLeaseProvider(cancellingPolicy);

            await AssertThrowsAsync<OperationCanceledException>(async () =>
                await provider.AcquireAsync(capturing.LastRequest!, cancellation.Token));

            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.IsTrue(cancellingPolicy.EvaluateCount >= 1);
            Assert.IsTrue(File.Exists(source));
            Assert.AreEqual("unchanged", await File.ReadAllTextAsync(source));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<FileDeleteOperationUserAuthorizationReceipt> AuthorizeAsync(
        string root,
        string source,
        IFileDeleteProtectedLocationPolicy policy)
    {
        var entry = new FileOperationEntry(source, Path.GetFileName(source), IsDirectory: false);
        var plan = new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                root,
                new[] { entry }));
        var validation = await new WindowsFileDeleteOperationExecutionValidator(
                protectedLocationPolicy: policy)
            .ValidateAsync(plan);
        Assert.AreEqual(
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            validation.Status);
        return new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
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

    private sealed class CancellingAllowedPolicy : IFileDeleteProtectedLocationPolicy
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingAllowedPolicy(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int EvaluateCount { get; private set; }

        public FileDeleteProtectedLocationResult Evaluate(string canonicalPath)
        {
            EvaluateCount++;
            _cancellation.Cancel();
            return new FileDeleteProtectedLocationResult(
                FileDeleteProtectedLocationDecision.AllowedForReview,
                "allowed while triggering provider cancellation regression");
        }
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
}
