using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileDeleteOperationSameHandleMutationTests
{
    [TestMethod]
    public async Task SameHandleMutationRemovesExactNamespaceAndCommitsIdentity()
    {
        using var fixture = new MutationFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "same-handle-delete");
        var authorization = await fixture.AuthorizeAsync();
        await using var historyStore = fixture.CreateHistoryStore();
        await historyStore.BeginAsync(authorization);

        var barrier = await fixture.AcquireBarrierAsync(authorization, historyStore);
        var result = await FileDeleteOperationMutationCommit.ExecuteAsync(
            barrier,
            historyStore);

        Assert.IsFalse(File.Exists(fixture.SourcePath));
        Assert.IsFalse(barrier.FinalLeaseHeld);
        Assert.IsFalse(barrier.DeleteAccessCapabilityHeld);
        Assert.IsFalse(barrier.DeleteMutationAuthorized);
        Assert.IsTrue(result.DeleteMutationPerformed);
        Assert.IsTrue(result.DurableCommitObserved);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        Assert.AreEqual(authorization.Items[0].Identity, result.DeletedSourceIdentity);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.Committed,
            result.CommittedHistory.Entries[0].State);
        Assert.AreEqual(
            authorization.Items[0].Identity,
            result.CommittedHistory.Entries[0].SourceIdentity);
        Assert.IsNull(result.CommittedHistory.TerminalState);

        var persisted = await historyStore.GetAsync(authorization.PlanId);
        Assert.IsNotNull(persisted);
        var persistedHistory = persisted!;
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.Committed,
            persistedHistory.Entries[0].State);
        Assert.IsNull(persistedHistory.TerminalState);
    }

    [TestMethod]
    public async Task PosixDispositionRemovesNamespaceWhileDeleteSharedReaderRemainsUsable()
    {
        using var fixture = new MutationFixture();
        const string content = "reader-survives-namespace-removal";
        await File.WriteAllTextAsync(fixture.SourcePath, content);
        await using var reader = new FileStream(
            fixture.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var authorization = await fixture.AuthorizeAsync();
        await using var historyStore = fixture.CreateHistoryStore();
        await historyStore.BeginAsync(authorization);
        var barrier = await fixture.AcquireBarrierAsync(authorization, historyStore);

        var result = await FileDeleteOperationMutationCommit.ExecuteAsync(
            barrier,
            historyStore);

        Assert.IsFalse(File.Exists(fixture.SourcePath));
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.Committed,
            result.CommittedHistory.Entries[0].State);

        reader.Position = 0;
        using var textReader = new StreamReader(
            reader,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        Assert.AreEqual(content, await textReader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task CancellationBeforeDestructiveTransferLeavesBarrierLiveAndFileUntouched()
    {
        using var fixture = new MutationFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "cancel-before-transfer");
        var authorization = await fixture.AuthorizeAsync();
        await using var historyStore = fixture.CreateHistoryStore();
        await historyStore.BeginAsync(authorization);
        var barrier = await fixture.AcquireBarrierAsync(authorization, historyStore);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await FileDeleteOperationMutationCommit.ExecuteAsync(
                barrier,
                historyStore,
                cancellation.Token));

        Assert.IsTrue(File.Exists(fixture.SourcePath));
        Assert.IsTrue(barrier.FinalLeaseHeld);
        Assert.IsTrue(barrier.DeleteAccessCapabilityHeld);
        Assert.IsTrue(barrier.DeleteMutationAuthorized);
        var persisted = await historyStore.GetAsync(authorization.PlanId);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.MutationStarted,
            persisted!.Entries[0].State);

        await barrier.DisposeAsync();
    }

    [TestMethod]
    public async Task ProtectedLocationPolicyIsRecheckedImmediatelyBeforeMutation()
    {
        using var fixture = new MutationFixture(useMutablePolicy: true);
        await File.WriteAllTextAsync(fixture.SourcePath, "block-at-mutation");
        var authorization = await fixture.AuthorizeAsync();
        await using var historyStore = fixture.CreateHistoryStore();
        await historyStore.BeginAsync(authorization);
        var barrier = await fixture.AcquireBarrierAsync(authorization, historyStore);

        fixture.MutablePolicy!.Blocked = true;
        await AssertThrowsAsync<UnauthorizedAccessException>(async () =>
            await FileDeleteOperationMutationCommit.ExecuteAsync(
                barrier,
                historyStore));

        Assert.IsTrue(File.Exists(fixture.SourcePath));
        Assert.IsFalse(barrier.FinalLeaseHeld);
        var persisted = await historyStore.GetAsync(authorization.PlanId);
        Assert.IsNotNull(persisted);
        var persistedHistory = persisted!;
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.RecoveryRequired,
            persistedHistory.Entries[0].State);
        Assert.AreEqual(
            "DeleteSameLeaseMutationFailed",
            persistedHistory.Entries[0].Failure!.Code);
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

    private sealed class MutationFixture : IDisposable
    {
        private const string FileName = "payload.tmp";
        private readonly IFileDeleteProtectedLocationPolicy _policy;

        public MutationFixture(bool useMutablePolicy = false)
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.SameHandleDelete.Tests",
                Guid.NewGuid().ToString("N"));
            HistoryRootPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.SameHandleDelete.History.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(HistoryRootPath);
            SourcePath = Path.Combine(RootPath, FileName);
            HistoryPath = Path.Combine(HistoryRootPath, "delete-history.sqlite");

            if (useMutablePolicy)
            {
                MutablePolicy = new MutableProtectedLocationPolicy();
                _policy = MutablePolicy;
            }
            else
            {
                _policy = new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>());
            }
        }

        public string RootPath { get; }

        public string SourcePath { get; }

        public string HistoryRootPath { get; }

        public string HistoryPath { get; }

        public MutableProtectedLocationPolicy? MutablePolicy { get; }

        public SqliteFileDeleteOperationActionHistoryStore CreateHistoryStore() =>
            new(HistoryPath);

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
                    protectedLocationPolicy: _policy)
                .ValidateAsync(plan);
            Assert.AreEqual(
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                validation.Status);

            return new FileDeleteOperationUserAuthorizationIssuer()
                .IssueAfterExplicitUserConfirmation(validation);
        }

        public async Task<FileDeleteOperationMutationBarrierScope> AcquireBarrierAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            IFileDeleteOperationActionHistoryStore historyStore)
        {
            var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                new WindowsFileDeleteOperationStabilityLeaseProvider(_policy),
                historyStore);
            var finalScope = await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
                preparation,
                new WindowsFileDeleteOperationFinalMutationLeaseProvider(_policy));
            return await FileDeleteOperationMutationBarrier.ClaimAsync(
                finalScope,
                historyStore);
        }

        public void Dispose()
        {
            DeleteDirectoryNoThrow(RootPath);
            DeleteDirectoryNoThrow(HistoryRootPath);
        }

        private static void DeleteDirectoryNoThrow(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class MutableProtectedLocationPolicy : IFileDeleteProtectedLocationPolicy
    {
        public bool Blocked { get; set; }

        public FileDeleteProtectedLocationResult Evaluate(string canonicalPath) =>
            Blocked
                ? new FileDeleteProtectedLocationResult(
                    FileDeleteProtectedLocationDecision.Blocked,
                    "blocked immediately before same-handle mutation")
                : new FileDeleteProtectedLocationResult(
                    FileDeleteProtectedLocationDecision.AllowedForReview,
                    "allowed for same-handle mutation test");
    }
}
