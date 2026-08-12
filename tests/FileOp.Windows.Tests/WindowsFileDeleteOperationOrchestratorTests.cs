using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileDeleteOperationOrchestratorTests
{
    [TestMethod]
    public async Task TwoAuthorizedFilesDeleteSequentiallyAndCompleteSucceededHistory()
    {
        using var fixture = new OrchestrationFixture();
        await File.WriteAllTextAsync(fixture.FirstPath, "first");
        await File.WriteAllTextAsync(fixture.SecondPath, "second");

        var authorization = await fixture.AuthorizeAsync();
        await using var historyStore = new SqliteFileDeleteOperationActionHistoryStore(fixture.HistoryPath);
        await historyStore.BeginAsync(authorization);

        var result = await FileDeleteOperationOrchestrator.ExecuteAsync(
            authorization,
            new WindowsFileDeleteOperationStabilityLeaseProvider(fixture.Policy),
            new WindowsFileDeleteOperationFinalMutationLeaseProvider(fixture.Policy),
            historyStore);

        Assert.IsFalse(File.Exists(fixture.FirstPath));
        Assert.IsFalse(File.Exists(fixture.SecondPath));
        Assert.AreEqual(2, result.MutatedEntryCount);
        Assert.AreEqual(0, result.PreviouslyTerminalEntryCount);
        Assert.IsFalse(result.CompletionObservedFromExistingHistory);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        Assert.AreEqual(
            FileDeleteOperationActionTerminalState.Succeeded,
            result.CompletedHistory.TerminalState);
        Assert.IsTrue(result.CompletedHistory.CompletedAtUtc.HasValue);
        Assert.IsTrue(result.CompletedHistory.Entries.All(static entry =>
            entry.State == FileDeleteOperationActionEntryState.Committed));
        for (var ordinal = 0; ordinal < authorization.Items.Count; ordinal++)
        {
            Assert.AreEqual(
                authorization.Items[ordinal].Identity,
                result.CompletedHistory.Entries[ordinal].SourceIdentity);
        }

        var persisted = await historyStore.GetAsync(authorization.PlanId);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(
            FileDeleteOperationActionTerminalState.Succeeded,
            persisted!.TerminalState);
        Assert.IsTrue(persisted.Entries.All(static entry =>
            entry.State == FileDeleteOperationActionEntryState.Committed));
    }

    private sealed class OrchestrationFixture : IDisposable
    {
        private const string FirstName = "first.tmp";
        private const string SecondName = "second.tmp";

        public OrchestrationFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.MultiEntryDelete.Tests",
                Guid.NewGuid().ToString("N"));
            HistoryRootPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.MultiEntryDelete.History.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(HistoryRootPath);
            FirstPath = Path.Combine(RootPath, FirstName);
            SecondPath = Path.Combine(RootPath, SecondName);
            HistoryPath = Path.Combine(HistoryRootPath, "delete-history.sqlite");
            Policy = new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>());
        }

        public string RootPath { get; }

        public string HistoryRootPath { get; }

        public string FirstPath { get; }

        public string SecondPath { get; }

        public string HistoryPath { get; }

        public IFileDeleteProtectedLocationPolicy Policy { get; }

        public async Task<FileDeleteOperationUserAuthorizationReceipt> AuthorizeAsync()
        {
            var entries = new[]
            {
                new FileOperationEntry(FirstPath, FirstName, IsDirectory: false),
                new FileOperationEntry(SecondPath, SecondName, IsDirectory: false),
            };
            var plan = new FileDeleteOperationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new FileDeleteOperationIntent(
                    "left",
                    Guid.NewGuid(),
                    RootPath,
                    entries));
            var validation = await new WindowsFileDeleteOperationExecutionValidator(
                    protectedLocationPolicy: Policy)
                .ValidateAsync(plan);
            Assert.AreEqual(
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                validation.Status);
            Assert.AreEqual(2, validation.Items.Count);
            Assert.AreEqual(2, validation.ReadyForAuthorizationReviewCount);

            return new FileDeleteOperationUserAuthorizationIssuer()
                .IssueAfterExplicitUserConfirmation(validation);
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
}
