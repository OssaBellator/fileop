using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

/// <summary>
/// Explicit signal that durable delete history is recovery-sensitive and must not be
/// interpreted as reusable filesystem-mutation authority.
/// </summary>
public sealed class FileDeleteOperationOrchestrationRecoveryRequiredException : Exception
{
    public FileDeleteOperationOrchestrationRecoveryRequiredException(
        string message,
        FileDeleteOperationActionHistory history,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(history);
        History = history;
    }

    public FileDeleteOperationActionHistory History { get; }

    public bool DeleteMutationAuthorized => false;
}

/// <summary>
/// Value-only evidence that one exact authorized multi-entry delete operation was observed
/// in a trustworthy durable terminal state. It carries no live lease or mutation authority.
/// </summary>
public sealed class FileDeleteOperationOrchestrationResult
{
    internal FileDeleteOperationOrchestrationResult(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory completedHistory,
        int mutatedEntryCount,
        int previouslyTerminalEntryCount,
        bool completionObservedFromExistingHistory)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(completedHistory);
        if (mutatedEntryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mutatedEntryCount));
        }
        if (previouslyTerminalEntryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(previouslyTerminalEntryCount));
        }

        Authorization = authorization;
        CompletedHistory = completedHistory;
        MutatedEntryCount = mutatedEntryCount;
        PreviouslyTerminalEntryCount = previouslyTerminalEntryCount;
        CompletionObservedFromExistingHistory = completionObservedFromExistingHistory;
    }

    public FileDeleteOperationUserAuthorizationReceipt Authorization { get; }

    public FileDeleteOperationActionHistory CompletedHistory { get; }

    public int MutatedEntryCount { get; }

    public int PreviouslyTerminalEntryCount { get; }

    public bool CompletionObservedFromExistingHistory { get; }

    public bool DeleteMutationAuthorized => false;
}

/// <summary>
/// Sequences the reviewed one-file delete pipeline across one exact session authorization.
/// Persisted action history is observation/recovery evidence only; it never recreates a lease,
/// request, barrier scope, or mutation authorization.
/// </summary>
public static class FileDeleteOperationOrchestrator
{
    public static async ValueTask<FileDeleteOperationOrchestrationResult> ExecuteAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        IFileDeleteOperationStabilityLeaseProvider stabilityLeaseProvider,
        IFileDeleteOperationFinalMutationLeaseProvider finalLeaseProvider,
        IFileDeleteOperationActionHistoryStore historyStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(stabilityLeaseProvider);
        ArgumentNullException.ThrowIfNull(finalLeaseProvider);
        ArgumentNullException.ThrowIfNull(historyStore);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAuthorization(authorization);

        var baseline = await historyStore
            .GetAsync(authorization.PlanId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Multi-entry delete orchestration requires an existing action-history operation begun from the exact authorization receipt.");
        ValidateInitialHistory(authorization, baseline);

        if (baseline.TerminalState.HasValue)
        {
            return CreateTerminalResult(
                authorization,
                baseline,
                mutatedEntryCount: 0,
                previouslyTerminalEntryCount: baseline.Entries.Count,
                completionObservedFromExistingHistory: true);
        }

        ThrowIfRecoverySensitive(baseline);

        var mutatedEntryCount = 0;
        var previouslyTerminalEntryCount = 0;

        for (var ordinal = 0; ordinal < authorization.Items.Count; ordinal++)
        {
            // Caller cancellation is honored only while no per-entry destructive barrier has
            // crossed. A completed prior ordinal is durable and will be observed on a later run.
            cancellationToken.ThrowIfCancellationRequested();

            var current = await LoadCurrentAsync(
                    authorization,
                    baseline,
                    historyStore,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.TerminalState.HasValue)
            {
                return CreateTerminalResult(
                    authorization,
                    current,
                    mutatedEntryCount,
                    previouslyTerminalEntryCount,
                    completionObservedFromExistingHistory: true);
            }

            ThrowIfRecoverySensitive(current);
            var entry = current.Entries[ordinal];
            switch (entry.State)
            {
                case FileDeleteOperationActionEntryState.Committed:
                case FileDeleteOperationActionEntryState.Failed:
                    previouslyTerminalEntryCount++;
                    continue;

                case FileDeleteOperationActionEntryState.Pending:
                    break;

                case FileDeleteOperationActionEntryState.MutationStarted:
                case FileDeleteOperationActionEntryState.RecoveryRequired:
                    throw new FileDeleteOperationOrchestrationRecoveryRequiredException(
                        $"Delete entry {ordinal} is recovery-sensitive; orchestration will not recreate mutation authority or advance to another file.",
                        current);

                default:
                    throw new InvalidOperationException(
                        $"Delete entry {ordinal} has unsupported orchestration state {entry.State}.");
            }

            await using var preparation = await FileDeleteOperationPreMutationPreparation
                .PrepareAsync(
                    authorization,
                    ordinal,
                    stabilityLeaseProvider,
                    historyStore,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var finalScope = await FileDeleteOperationFinalMutationLeasePreparation
                .AcquireAsync(
                    preparation,
                    finalLeaseProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var barrierScope = await FileDeleteOperationMutationBarrier
                .ClaimAsync(
                    finalScope,
                    historyStore,
                    cancellationToken)
                .ConfigureAwait(false);

            // MutationStarted is already durable once ClaimAsync returns. From this point the
            // exact one-file primitive owns mutation/release/settlement/recovery and must not be
            // interrupted by a caller token that could manufacture avoidable ambiguity.
            var committed = await FileDeleteOperationMutationCommit
                .ExecuteAsync(
                    barrierScope,
                    historyStore,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ValidatePerEntryCommit(authorization, baseline, ordinal, committed);
            mutatedEntryCount++;
        }

        // Once every ordinal has been observed/settled, finish durable operation bookkeeping in
        // one non-cancellable section. No filesystem authority exists here.
        var finalHistory = await LoadCurrentAsync(
                authorization,
                baseline,
                historyStore,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (finalHistory.TerminalState.HasValue)
        {
            return CreateTerminalResult(
                authorization,
                finalHistory,
                mutatedEntryCount,
                previouslyTerminalEntryCount,
                completionObservedFromExistingHistory: true);
        }

        ThrowIfRecoverySensitive(finalHistory);
        if (finalHistory.Entries.Any(static entry => entry.State == FileDeleteOperationActionEntryState.Pending))
        {
            throw new InvalidOperationException(
                "Multi-entry delete orchestration reached completion with one or more Pending entries.");
        }
        if (finalHistory.Entries.Any(static entry =>
                entry.State is not FileDeleteOperationActionEntryState.Committed and
                    not FileDeleteOperationActionEntryState.Failed))
        {
            throw new InvalidOperationException(
                "Multi-entry delete orchestration can complete only after every entry is Committed or Failed.");
        }

        try
        {
            var completed = await historyStore
                .CompleteAsync(authorization.PlanId, CancellationToken.None)
                .ConfigureAwait(false);
            ValidateHistoryAgainstBaseline(authorization, baseline, completed);
            return CreateTerminalResult(
                authorization,
                completed,
                mutatedEntryCount,
                previouslyTerminalEntryCount,
                completionObservedFromExistingHistory: false);
        }
        catch (FileDeleteOperationOrchestrationRecoveryRequiredException)
        {
            throw;
        }
        catch (Exception completionException)
        {
            return await ResolveCompletionOutcomeAsync(
                    authorization,
                    baseline,
                    historyStore,
                    mutatedEntryCount,
                    previouslyTerminalEntryCount,
                    completionException)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<FileDeleteOperationOrchestrationResult> ResolveCompletionOutcomeAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory baseline,
        IFileDeleteOperationActionHistoryStore historyStore,
        int mutatedEntryCount,
        int previouslyTerminalEntryCount,
        Exception completionException)
    {
        FileDeleteOperationActionHistory? observed;
        try
        {
            observed = await historyStore
                .GetAsync(authorization.PlanId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception inspectionException)
        {
            throw new AggregateException(
                "Delete entries are terminal but operation completion threw and its durable outcome could not be inspected.",
                completionException,
                inspectionException);
        }

        if (observed is null)
        {
            throw new AggregateException(
                "Delete entries are terminal but operation history disappeared while inspecting completion outcome.",
                completionException,
                new InvalidOperationException("Delete action history no longer exists."));
        }

        ValidateHistoryAgainstBaseline(authorization, baseline, observed);
        if (observed.TerminalState.HasValue)
        {
            return CreateTerminalResult(
                authorization,
                observed,
                mutatedEntryCount,
                previouslyTerminalEntryCount,
                completionObservedFromExistingHistory: true);
        }

        ThrowIfRecoverySensitive(observed);
        if (observed.Entries.All(static entry =>
                entry.State is FileDeleteOperationActionEntryState.Committed or
                    FileDeleteOperationActionEntryState.Failed))
        {
            throw new InvalidOperationException(
                "All delete entries are durably terminal but operation completion was not proven. A later orchestration call may retry completion without replaying filesystem mutation.",
                completionException);
        }

        throw new AggregateException(
            "Delete operation completion failed and the observed durable state was not a trustworthy terminal or retryable completion-only state.",
            completionException,
            new InvalidOperationException("Observed delete history contains non-terminal entry state."));
    }

    private static async ValueTask<FileDeleteOperationActionHistory> LoadCurrentAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory baseline,
        IFileDeleteOperationActionHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        var history = await historyStore
            .GetAsync(authorization.PlanId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Delete action history disappeared during multi-entry orchestration.");
        ValidateHistoryAgainstBaseline(authorization, baseline, history);
        return history;
    }

    private static void ValidateAuthorization(
        FileDeleteOperationUserAuthorizationReceipt authorization)
    {
        if (!authorization.UserAuthorizedAttempt ||
            authorization.DeleteMutationAuthorized ||
            authorization.PlanId == Guid.Empty ||
            authorization.AuthorizationId == Guid.Empty ||
            authorization.Items.Count == 0 ||
            authorization.Items.Count != authorization.Plan.Intent.Entries.Count ||
            string.IsNullOrWhiteSpace(authorization.CanonicalSourceDirectoryPath))
        {
            throw new ArgumentException(
                "Multi-entry delete orchestration requires one complete non-mutating session authorization receipt.",
                nameof(authorization));
        }

        for (var ordinal = 0; ordinal < authorization.Items.Count; ordinal++)
        {
            var item = authorization.Items[ordinal];
            if (item.Entry != authorization.Plan.Intent.Entries[ordinal] ||
                item.Entry.IsDirectory ||
                string.IsNullOrWhiteSpace(item.CanonicalPath))
            {
                throw new ArgumentException(
                    "Multi-entry delete orchestration requires exact ordered authorized file evidence.",
                    nameof(authorization));
            }
        }
    }

    private static void ValidateInitialHistory(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory history)
    {
        if (history.OperationId != authorization.PlanId ||
            history.AuthorizationId != authorization.AuthorizationId ||
            history.QueuedAtUtc != authorization.Plan.QueuedAtUtc.ToUniversalTime() ||
            history.ValidatedAtUtc != authorization.ValidatedAtUtc.ToUniversalTime() ||
            history.AuthorizedAtUtc != authorization.AuthorizedAtUtc.ToUniversalTime() ||
            !string.Equals(history.SourcePaneId, authorization.Plan.Intent.SourcePane, StringComparison.Ordinal) ||
            history.SourceTabId != authorization.Plan.Intent.SourceTabId ||
            !PathEquals(history.CanonicalSourceDirectoryPath, authorization.CanonicalSourceDirectoryPath) ||
            history.SourceDirectoryIdentity != authorization.SourceDirectoryIdentity ||
            history.Entries.Count != authorization.Items.Count)
        {
            throw new InvalidOperationException(
                "Delete action history is not bound to the exact orchestration authorization provenance.");
        }

        ValidateEntryProvenance(authorization, history);
        if (history.TerminalState.HasValue != history.CompletedAtUtc.HasValue)
        {
            throw new InvalidOperationException(
                "Delete action history terminal state/completion timestamp shape is inconsistent.");
        }
    }

    private static void ValidateHistoryAgainstBaseline(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory baseline,
        FileDeleteOperationActionHistory history)
    {
        ValidateInitialHistory(authorization, history);
        if (history.OperationId != baseline.OperationId ||
            history.AuthorizationId != baseline.AuthorizationId ||
            history.QueuedAtUtc != baseline.QueuedAtUtc ||
            history.ValidatedAtUtc != baseline.ValidatedAtUtc ||
            history.AuthorizedAtUtc != baseline.AuthorizedAtUtc ||
            history.StartedAtUtc != baseline.StartedAtUtc ||
            !string.Equals(history.SourcePaneId, baseline.SourcePaneId, StringComparison.Ordinal) ||
            history.SourceTabId != baseline.SourceTabId ||
            !PathEquals(history.CanonicalSourceDirectoryPath, baseline.CanonicalSourceDirectoryPath) ||
            history.SourceDirectoryIdentity != baseline.SourceDirectoryIdentity ||
            history.Entries.Count != baseline.Entries.Count)
        {
            throw new InvalidOperationException(
                "Delete action history changed static operation provenance during orchestration.");
        }

        for (var ordinal = 0; ordinal < baseline.Entries.Count; ordinal++)
        {
            var prior = baseline.Entries[ordinal];
            var current = history.Entries[ordinal];
            if (current.Ordinal != prior.Ordinal ||
                current.Entry != prior.Entry ||
                !PathEquals(current.CanonicalSourcePath, prior.CanonicalSourcePath) ||
                current.SourceIdentity != prior.SourceIdentity)
            {
                throw new InvalidOperationException(
                    $"Delete action history changed static entry provenance for ordinal {ordinal}.");
            }
        }
    }

    private static void ValidateEntryProvenance(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory history)
    {
        for (var ordinal = 0; ordinal < authorization.Items.Count; ordinal++)
        {
            var item = authorization.Items[ordinal];
            var entry = history.Entries[ordinal];
            if (entry.Ordinal != ordinal ||
                entry.Entry != item.Entry ||
                !PathEquals(entry.CanonicalSourcePath, item.CanonicalPath) ||
                entry.SourceIdentity != item.Identity)
            {
                throw new InvalidOperationException(
                    $"Delete action history entry {ordinal} does not match the exact authorized file provenance.");
            }
        }
    }

    private static void ValidatePerEntryCommit(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory baseline,
        int ordinal,
        FileDeleteOperationMutationCommitResult committed)
    {
        if (!ReferenceEquals(committed.Authorization, authorization) ||
            committed.Ordinal != ordinal ||
            committed.DeletedSourceIdentity != authorization.Items[ordinal].Identity ||
            !committed.DeleteMutationPerformed ||
            !committed.DurableCommitObserved ||
            committed.DeleteMutationAuthorized)
        {
            throw new InvalidOperationException(
                "Per-entry delete mutation result is not bound to the exact orchestration authorization and ordinal.");
        }

        ValidateHistoryAgainstBaseline(
            authorization,
            baseline,
            committed.CommittedHistory);
        if (committed.CommittedHistory.Entries[ordinal].State !=
            FileDeleteOperationActionEntryState.Committed)
        {
            throw new InvalidOperationException(
                "Per-entry delete mutation did not return the exact selected Committed history entry.");
        }
    }

    private static FileDeleteOperationOrchestrationResult CreateTerminalResult(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory history,
        int mutatedEntryCount,
        int previouslyTerminalEntryCount,
        bool completionObservedFromExistingHistory)
    {
        ValidateTerminalHistory(history);
        if (history.TerminalState == FileDeleteOperationActionTerminalState.RecoveryRequired)
        {
            throw new FileDeleteOperationOrchestrationRecoveryRequiredException(
                "Delete operation history is terminal but recovery-sensitive; no mutation authority will be recreated.",
                history);
        }

        return new FileDeleteOperationOrchestrationResult(
            authorization,
            history,
            mutatedEntryCount,
            previouslyTerminalEntryCount,
            completionObservedFromExistingHistory);
    }

    private static void ValidateTerminalHistory(FileDeleteOperationActionHistory history)
    {
        if (!history.TerminalState.HasValue || !history.CompletedAtUtc.HasValue)
        {
            throw new InvalidOperationException(
                "Delete orchestration terminal result requires a durable terminal state and completion timestamp.");
        }

        switch (history.TerminalState.Value)
        {
            case FileDeleteOperationActionTerminalState.Succeeded:
                if (history.Entries.Any(static entry =>
                    entry.State != FileDeleteOperationActionEntryState.Committed))
                {
                    throw new InvalidOperationException(
                        "Succeeded delete operation history requires every entry to be Committed.");
                }
                break;

            case FileDeleteOperationActionTerminalState.Failed:
                if (!history.Entries.Any(static entry =>
                        entry.State == FileDeleteOperationActionEntryState.Failed) ||
                    history.Entries.Any(static entry =>
                        entry.State is FileDeleteOperationActionEntryState.Pending or
                            FileDeleteOperationActionEntryState.MutationStarted or
                            FileDeleteOperationActionEntryState.RecoveryRequired))
                {
                    throw new InvalidOperationException(
                        "Failed delete operation history requires at least one Failed entry and no pending/recovery-sensitive entries.");
                }
                break;

            case FileDeleteOperationActionTerminalState.RecoveryRequired:
                if (!history.Entries.Any(static entry =>
                        entry.State is FileDeleteOperationActionEntryState.MutationStarted or
                            FileDeleteOperationActionEntryState.RecoveryRequired))
                {
                    throw new InvalidOperationException(
                        "RecoveryRequired delete operation history must retain recovery-sensitive entry evidence.");
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(history));
        }
    }

    private static void ThrowIfRecoverySensitive(FileDeleteOperationActionHistory history)
    {
        if (history.Entries.Any(static entry =>
            entry.State is FileDeleteOperationActionEntryState.MutationStarted or
                FileDeleteOperationActionEntryState.RecoveryRequired))
        {
            throw new FileDeleteOperationOrchestrationRecoveryRequiredException(
                "Delete action history is recovery-sensitive; orchestration will not recreate mutation authority or advance to another file.",
                history);
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}