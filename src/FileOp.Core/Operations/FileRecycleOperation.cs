using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public sealed record FileRecycleOperationIntent
{
    public FileRecycleOperationIntent(
        string sourcePane,
        Guid sourceTabId,
        string sourceDirectoryPath,
        IEnumerable<FileOperationEntry>? entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePane);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);
        ArgumentNullException.ThrowIfNull(entries);

        var snapshot = entries.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("Recycle requires at least one captured source file.", nameof(entries));
        }

        foreach (var entry in snapshot)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Path);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Name);
            if (entry.IsDirectory)
            {
                throw new ArgumentException("This recycle transaction is regular-file-only.", nameof(entries));
            }
        }

        SourcePane = sourcePane;
        SourceTabId = sourceTabId;
        SourceDirectoryPath = sourceDirectoryPath;
        Entries = Array.AsReadOnly(snapshot);
    }

    public string SourcePane { get; }
    public Guid SourceTabId { get; }
    public string SourceDirectoryPath { get; }
    public IReadOnlyList<FileOperationEntry> Entries { get; }
}

public sealed record FileRecycleOperationPlan
{
    public FileRecycleOperationPlan(Guid id, DateTimeOffset queuedAtUtc, FileRecycleOperationIntent intent)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Recycle operation ID cannot be empty.", nameof(id));
        }
        ArgumentNullException.ThrowIfNull(intent);

        Id = id;
        QueuedAtUtc = queuedAtUtc.ToUniversalTime();
        Intent = intent;
    }

    public Guid Id { get; }
    public DateTimeOffset QueuedAtUtc { get; }
    public FileRecycleOperationIntent Intent { get; }
}

public sealed record FileRecycleOperationReviewItem(
    FileOperationEntry Entry,
    string CanonicalPath,
    FileIdentity Identity);

public sealed class FileRecycleOperationReview
{
    public FileRecycleOperationReview(
        FileRecycleOperationPlan plan,
        string canonicalSourceDirectoryPath,
        FileIdentity sourceDirectoryIdentity,
        IEnumerable<FileRecycleOperationReviewItem>? items,
        DateTimeOffset reviewedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentNullException.ThrowIfNull(items);

        var snapshot = items.ToArray();
        if (snapshot.Length != plan.Intent.Entries.Count)
        {
            throw new ArgumentException("Recycle review must cover every captured file in exact plan order.", nameof(items));
        }

        for (var index = 0; index < snapshot.Length; index++)
        {
            var item = snapshot[index];
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.CanonicalPath);
            if (item.Entry != plan.Intent.Entries[index] || item.Entry.IsDirectory)
            {
                throw new ArgumentException("Recycle review evidence must preserve the exact file-only plan order.", nameof(items));
            }
        }

        Plan = plan;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        Items = Array.AsReadOnly(snapshot);
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
    }

    public FileRecycleOperationPlan Plan { get; }
    public string CanonicalSourceDirectoryPath { get; }
    public FileIdentity SourceDirectoryIdentity { get; }
    public IReadOnlyList<FileRecycleOperationReviewItem> Items { get; }
    public DateTimeOffset ReviewedAtUtc { get; }
    public bool RecycleMutationAuthorized => false;
}

public sealed class FileRecycleOperationUserAuthorizationReceipt
{
    internal FileRecycleOperationUserAuthorizationReceipt(
        Guid authorizationId,
        FileRecycleOperationReview review,
        DateTimeOffset authorizedAtUtc)
    {
        if (authorizationId == Guid.Empty)
        {
            throw new ArgumentException("Recycle authorization ID cannot be empty.", nameof(authorizationId));
        }
        ArgumentNullException.ThrowIfNull(review);

        AuthorizationId = authorizationId;
        Review = review;
        AuthorizedAtUtc = authorizedAtUtc.ToUniversalTime();
    }

    public Guid AuthorizationId { get; }
    public FileRecycleOperationReview Review { get; }
    public FileRecycleOperationPlan Plan => Review.Plan;
    public Guid PlanId => Review.Plan.Id;
    public DateTimeOffset AuthorizedAtUtc { get; }
    public bool UserAuthorizedAttempt => true;
    public bool RecycleMutationAuthorized => false;
}

public interface IFileRecycleOperationUserAuthorizationIssuer
{
    FileRecycleOperationUserAuthorizationReceipt IssueAfterExplicitUserConfirmation(
        FileRecycleOperationReview review);
}

public sealed class FileRecycleOperationUserAuthorizationIssuer : IFileRecycleOperationUserAuthorizationIssuer
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _authorizationIdFactory;

    public FileRecycleOperationUserAuthorizationIssuer()
        : this(TimeProvider.System, Guid.NewGuid)
    {
    }

    public FileRecycleOperationUserAuthorizationIssuer(TimeProvider timeProvider, Func<Guid> authorizationIdFactory)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(authorizationIdFactory);
        _timeProvider = timeProvider;
        _authorizationIdFactory = authorizationIdFactory;
    }

    public FileRecycleOperationUserAuthorizationReceipt IssueAfterExplicitUserConfirmation(
        FileRecycleOperationReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var authorizationId = _authorizationIdFactory();
        if (authorizationId == Guid.Empty)
        {
            throw new InvalidOperationException("Recycle authorization ID source returned an empty ID.");
        }

        return new FileRecycleOperationUserAuthorizationReceipt(
            authorizationId,
            review,
            _timeProvider.GetUtcNow());
    }
}

public enum FileRecycleFreshIdentityStatus
{
    Ready,
    Blocked,
}

public sealed class FileRecycleFreshIdentityResult
{
    private FileRecycleFreshIdentityResult(
        FileRecycleFreshIdentityStatus status,
        FileRecycleOperationUserAuthorizationReceipt authorization,
        DateTimeOffset validatedAtUtc,
        FileOperationFailure? failure)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        ArgumentNullException.ThrowIfNull(authorization);
        if ((status == FileRecycleFreshIdentityStatus.Ready) == (failure is not null))
        {
            throw new ArgumentException("Ready recycle freshness evidence cannot contain a failure; blocked evidence must contain one.", nameof(failure));
        }
        if (failure is not null)
        {
            ValidateFailure(failure);
        }

        Status = status;
        Authorization = authorization;
        ValidatedAtUtc = validatedAtUtc.ToUniversalTime();
        Failure = failure;
    }

    public FileRecycleFreshIdentityStatus Status { get; }
    public FileRecycleOperationUserAuthorizationReceipt Authorization { get; }
    public DateTimeOffset ValidatedAtUtc { get; }
    public FileOperationFailure? Failure { get; }
    public bool CanBeginDurableHistory => Status == FileRecycleFreshIdentityStatus.Ready;
    public bool RecycleMutationAuthorized => false;

    public bool IsBoundTo(FileRecycleOperationUserAuthorizationReceipt authorization) =>
        ReferenceEquals(Authorization, authorization);

    public static FileRecycleFreshIdentityResult Ready(
        FileRecycleOperationUserAuthorizationReceipt authorization,
        string canonicalSourceDirectoryPath,
        FileIdentity sourceDirectoryIdentity,
        IReadOnlyList<FileRecycleOperationReviewItem> currentItems,
        DateTimeOffset validatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentNullException.ThrowIfNull(currentItems);

        var review = authorization.Review;
        if (!string.Equals(canonicalSourceDirectoryPath, review.CanonicalSourceDirectoryPath, StringComparison.Ordinal) ||
            sourceDirectoryIdentity != review.SourceDirectoryIdentity ||
            currentItems.Count != review.Items.Count)
        {
            throw new ArgumentException("Fresh recycle identity evidence must match the exact reviewed root and file set.");
        }

        for (var index = 0; index < currentItems.Count; index++)
        {
            var current = currentItems[index];
            var reviewed = review.Items[index];
            ArgumentNullException.ThrowIfNull(current);
            if (current.Entry != reviewed.Entry ||
                !string.Equals(current.CanonicalPath, reviewed.CanonicalPath, StringComparison.Ordinal) ||
                current.Identity != reviewed.Identity)
            {
                throw new ArgumentException("Fresh recycle identity evidence must match every reviewed file in exact order.", nameof(currentItems));
            }
        }

        return new FileRecycleFreshIdentityResult(
            FileRecycleFreshIdentityStatus.Ready,
            authorization,
            validatedAtUtc,
            failure: null);
    }

    public static FileRecycleFreshIdentityResult Blocked(
        FileRecycleOperationUserAuthorizationReceipt authorization,
        DateTimeOffset validatedAtUtc,
        FileOperationFailure failure) =>
        new(FileRecycleFreshIdentityStatus.Blocked, authorization, validatedAtUtc, failure);

    private static void ValidateFailure(FileOperationFailure failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Message);
    }
}

public interface IFileRecycleFreshIdentityValidator
{
    ValueTask<FileRecycleFreshIdentityResult> ValidateAsync(
        FileRecycleOperationUserAuthorizationReceipt authorization,
        CancellationToken cancellationToken = default);
}

public sealed record FileRecycleMutationRequest(
    Guid OperationId,
    Guid AuthorizationId,
    int Ordinal,
    string CanonicalSourcePath,
    FileIdentity SourceIdentity);

public enum FileRecycleMutationDisposition
{
    Recycled,
    NotMutated,
    Ambiguous,
}

public sealed class FileRecycleMutationResult
{
    private FileRecycleMutationResult(
        FileRecycleMutationDisposition disposition,
        FileIdentity sourceIdentity,
        string providerName,
        string? recycleLocator,
        FileOperationFailure? failure)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (failure is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failure.Code);
            ArgumentException.ThrowIfNullOrWhiteSpace(failure.Message);
        }

        var valid = disposition switch
        {
            FileRecycleMutationDisposition.Recycled => !string.IsNullOrWhiteSpace(recycleLocator) && failure is null,
            FileRecycleMutationDisposition.NotMutated => recycleLocator is null && failure is not null,
            FileRecycleMutationDisposition.Ambiguous => failure is not null,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException("Recycle provider result evidence is inconsistent with its disposition.");
        }

        Disposition = disposition;
        SourceIdentity = sourceIdentity;
        ProviderName = providerName;
        RecycleLocator = recycleLocator;
        Failure = failure;
    }

    public FileRecycleMutationDisposition Disposition { get; }
    public FileIdentity SourceIdentity { get; }
    public string ProviderName { get; }
    public string? RecycleLocator { get; }
    public FileOperationFailure? Failure { get; }
    public bool RestoreAuthorized => false;

    public static FileRecycleMutationResult Recycled(
        FileIdentity sourceIdentity,
        string providerName,
        string recycleLocator) =>
        new(FileRecycleMutationDisposition.Recycled, sourceIdentity, providerName, recycleLocator, failure: null);

    public static FileRecycleMutationResult NotMutated(
        FileIdentity sourceIdentity,
        string providerName,
        FileOperationFailure failure) =>
        new(FileRecycleMutationDisposition.NotMutated, sourceIdentity, providerName, recycleLocator: null, failure);

    public static FileRecycleMutationResult Ambiguous(
        FileIdentity sourceIdentity,
        string providerName,
        FileOperationFailure failure,
        string? observedRecycleLocator = null) =>
        new(FileRecycleMutationDisposition.Ambiguous, sourceIdentity, providerName, observedRecycleLocator, failure);
}

public interface IFileRecycleMutationProvider
{
    string ProviderName { get; }
    ValueTask<FileRecycleMutationResult> RecycleAsync(FileRecycleMutationRequest request);
}

public enum FileRecycleActionEntryState
{
    Pending,
    MutationStarted,
    Recycled,
    Failed,
    RecoveryRequired,
}

public enum FileRecycleActionTerminalState
{
    Succeeded,
    Failed,
    Cancelled,
    RecoveryRequired,
}

public sealed record FileRecycleActionEntry(
    int Ordinal,
    FileOperationEntry Entry,
    string CanonicalSourcePath,
    FileIdentity SourceIdentity,
    FileRecycleActionEntryState State,
    DateTimeOffset? MutationStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ProviderName,
    string? RecycleLocator,
    FileOperationFailure? Failure)
{
    public bool RestoreAuthorized => false;
}

public sealed class FileRecycleActionHistory
{
    public FileRecycleActionHistory(
        Guid operationId,
        Guid authorizationId,
        DateTimeOffset queuedAtUtc,
        DateTimeOffset reviewedAtUtc,
        DateTimeOffset authorizedAtUtc,
        DateTimeOffset freshValidatedAtUtc,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string sourcePaneId,
        Guid sourceTabId,
        string canonicalSourceDirectoryPath,
        FileIdentity sourceDirectoryIdentity,
        FileRecycleActionTerminalState? terminalState,
        IEnumerable<FileRecycleActionEntry>? entries)
    {
        if (operationId == Guid.Empty || authorizationId == Guid.Empty)
        {
            throw new ArgumentException("Recycle history requires non-empty operation and authorization IDs.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePaneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentNullException.ThrowIfNull(entries);
        if (terminalState.HasValue != completedAtUtc.HasValue)
        {
            throw new ArgumentException("Recycle history terminal state and completion time must appear together.");
        }

        var snapshot = entries.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("Recycle history requires at least one entry.", nameof(entries));
        }
        for (var index = 0; index < snapshot.Length; index++)
        {
            ValidateEntry(snapshot[index], index);
        }
        ValidateEntryOrdering(snapshot);
        ValidateTerminal(snapshot, terminalState);

        OperationId = operationId;
        AuthorizationId = authorizationId;
        QueuedAtUtc = queuedAtUtc.ToUniversalTime();
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
        AuthorizedAtUtc = authorizedAtUtc.ToUniversalTime();
        FreshValidatedAtUtc = freshValidatedAtUtc.ToUniversalTime();
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAtUtc?.ToUniversalTime();
        SourcePaneId = sourcePaneId;
        SourceTabId = sourceTabId;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        TerminalState = terminalState;
        Entries = Array.AsReadOnly(snapshot);
    }

    public Guid OperationId { get; }
    public Guid AuthorizationId { get; }
    public DateTimeOffset QueuedAtUtc { get; }
    public DateTimeOffset ReviewedAtUtc { get; }
    public DateTimeOffset AuthorizedAtUtc { get; }
    public DateTimeOffset FreshValidatedAtUtc { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset? CompletedAtUtc { get; }
    public string SourcePaneId { get; }
    public Guid SourceTabId { get; }
    public string CanonicalSourceDirectoryPath { get; }
    public FileIdentity SourceDirectoryIdentity { get; }
    public FileRecycleActionTerminalState? TerminalState { get; }
    public IReadOnlyList<FileRecycleActionEntry> Entries { get; }
    public bool IsTerminal => TerminalState.HasValue;
    public bool IsRecoverySensitive => TerminalState == FileRecycleActionTerminalState.RecoveryRequired || Entries.Any(static item => item.State is FileRecycleActionEntryState.MutationStarted or FileRecycleActionEntryState.RecoveryRequired);
    public bool RestoreAuthorized => false;

    internal static void ValidateEntry(FileRecycleActionEntry entry, int expectedOrdinal)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.CanonicalSourcePath);
        if (entry.Ordinal != expectedOrdinal || entry.Entry.IsDirectory || !Enum.IsDefined(entry.State))
        {
            throw new ArgumentException("Recycle history entries must be ordered regular-file evidence.", nameof(entry));
        }
        if (entry.Failure is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Failure.Code);
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.Failure.Message);
        }

        var started = entry.MutationStartedAtUtc.HasValue;
        var completed = entry.CompletedAtUtc.HasValue;
        var provider = !string.IsNullOrWhiteSpace(entry.ProviderName);
        var locator = !string.IsNullOrWhiteSpace(entry.RecycleLocator);
        var failure = entry.Failure is not null;
        var valid = entry.State switch
        {
            FileRecycleActionEntryState.Pending => !started && !completed && !provider && !locator && !failure,
            FileRecycleActionEntryState.MutationStarted => started && !completed && !provider && !locator && !failure,
            FileRecycleActionEntryState.Recycled => started && completed && provider && locator && !failure,
            FileRecycleActionEntryState.Failed => started && completed && provider && !locator && failure,
            FileRecycleActionEntryState.RecoveryRequired => started && completed && provider && failure,
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"Recycle history entry {entry.Ordinal} has invalid evidence for state {entry.State}.", nameof(entry));
        }
    }

    private static void ValidateEntryOrdering(IReadOnlyList<FileRecycleActionEntry> entries)
    {
        var frontierSeen = false;
        var pendingSeen = false;
        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case FileRecycleActionEntryState.Recycled:
                    if (frontierSeen || pendingSeen)
                    {
                        throw new ArgumentException("Recycle history must contain one contiguous recycled prefix.", nameof(entries));
                    }
                    break;

                case FileRecycleActionEntryState.Pending:
                    pendingSeen = true;
                    break;

                case FileRecycleActionEntryState.MutationStarted:
                case FileRecycleActionEntryState.Failed:
                case FileRecycleActionEntryState.RecoveryRequired:
                    if (frontierSeen || pendingSeen)
                    {
                        throw new ArgumentException("Recycle history may contain at most one non-terminal frontier immediately after the recycled prefix.", nameof(entries));
                    }
                    frontierSeen = true;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(entry.State));
            }
        }
    }

    private static void ValidateTerminal(IReadOnlyList<FileRecycleActionEntry> entries, FileRecycleActionTerminalState? terminalState)
    {
        if (!terminalState.HasValue)
        {
            return;
        }

        switch (terminalState.Value)
        {
            case FileRecycleActionTerminalState.Succeeded:
                if (entries.Any(static item => item.State != FileRecycleActionEntryState.Recycled))
                {
                    throw new ArgumentException("Successful recycle history requires every entry recycled.", nameof(terminalState));
                }
                break;

            case FileRecycleActionTerminalState.Failed:
                if (entries.Count(static item => item.State == FileRecycleActionEntryState.Failed) != 1 ||
                    entries.Any(static item => item.State is FileRecycleActionEntryState.MutationStarted or FileRecycleActionEntryState.RecoveryRequired))
                {
                    throw new ArgumentException("Failed recycle history requires exactly one definite non-mutated frontier and no recovery-sensitive entry.", nameof(terminalState));
                }
                break;

            case FileRecycleActionTerminalState.Cancelled:
                if (!entries.Any(static item => item.State == FileRecycleActionEntryState.Pending) ||
                    entries.Any(static item => item.State is FileRecycleActionEntryState.MutationStarted or FileRecycleActionEntryState.Failed or FileRecycleActionEntryState.RecoveryRequired))
                {
                    throw new ArgumentException("Cancelled recycle history requires a recycled prefix followed by at least one pending entry.", nameof(terminalState));
                }
                break;

            case FileRecycleActionTerminalState.RecoveryRequired:
                if (entries.Count(static item => item.State is FileRecycleActionEntryState.MutationStarted or FileRecycleActionEntryState.RecoveryRequired) != 1 ||
                    entries.Any(static item => item.State == FileRecycleActionEntryState.Failed))
                {
                    throw new ArgumentException("RecoveryRequired recycle history requires exactly one recovery-sensitive frontier and no definite-failure frontier.", nameof(terminalState));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(terminalState));
        }
    }
}

public interface IFileRecycleActionHistoryStore : IAsyncDisposable
{
    ValueTask<FileRecycleActionHistory> BeginAsync(FileRecycleOperationUserAuthorizationReceipt authorization, FileRecycleFreshIdentityResult freshValidation, CancellationToken cancellationToken = default);
    ValueTask<FileRecycleActionHistory> MarkMutationStartedAsync(Guid operationId, int ordinal, CancellationToken cancellationToken = default);
    ValueTask<FileRecycleActionHistory> CommitRecycledAsync(Guid operationId, int ordinal, FileIdentity sourceIdentity, string providerName, string recycleLocator, CancellationToken cancellationToken = default);
    ValueTask<FileRecycleActionHistory> MarkFailedAsync(Guid operationId, int ordinal, FileIdentity sourceIdentity, string providerName, FileOperationFailure failure, CancellationToken cancellationToken = default);
    ValueTask<FileRecycleActionHistory> MarkRecoveryRequiredAsync(Guid operationId, int ordinal, FileIdentity sourceIdentity, string providerName, string? recycleLocator, FileOperationFailure failure, CancellationToken cancellationToken = default);
    ValueTask<FileRecycleActionHistory> CompleteAsync(Guid operationId, FileRecycleActionTerminalState terminalState, CancellationToken cancellationToken = default);
    ValueTask<FileRecycleActionHistory?> GetAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public enum FileRecycleExecutionStatus
{
    Succeeded,
    Blocked,
    Failed,
    Cancelled,
    RecoveryRequired,
}

public sealed record FileRecycleExecutionResult(
    FileRecycleExecutionStatus Status,
    FileRecycleFreshIdentityResult FreshValidation,
    FileRecycleActionHistory? History,
    FileOperationFailure? Failure);

public sealed class FileRecycleOperationExecutor
{
    private readonly IFileRecycleFreshIdentityValidator _freshIdentityValidator;
    private readonly IFileRecycleActionHistoryStore _historyStore;
    private readonly IFileRecycleMutationProvider _mutationProvider;
    private readonly string _providerName;

    public FileRecycleOperationExecutor(
        IFileRecycleFreshIdentityValidator freshIdentityValidator,
        IFileRecycleActionHistoryStore historyStore,
        IFileRecycleMutationProvider mutationProvider)
    {
        ArgumentNullException.ThrowIfNull(freshIdentityValidator);
        ArgumentNullException.ThrowIfNull(historyStore);
        ArgumentNullException.ThrowIfNull(mutationProvider);
        var providerName = mutationProvider.ProviderName;
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        _freshIdentityValidator = freshIdentityValidator;
        _historyStore = historyStore;
        _mutationProvider = mutationProvider;
        _providerName = providerName;
    }

    public async ValueTask<FileRecycleExecutionResult> ExecuteAsync(
        FileRecycleOperationUserAuthorizationReceipt authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        cancellationToken.ThrowIfCancellationRequested();

        var fresh = await _freshIdentityValidator.ValidateAsync(authorization, cancellationToken).ConfigureAwait(false);
        if (!fresh.IsBoundTo(authorization))
        {
            throw new InvalidOperationException("Recycle fresh-identity validator returned evidence for a different authorization receipt.");
        }
        if (!fresh.CanBeginDurableHistory)
        {
            return new FileRecycleExecutionResult(FileRecycleExecutionStatus.Blocked, fresh, History: null, fresh.Failure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var history = await _historyStore.BeginAsync(authorization, fresh, cancellationToken).ConfigureAwait(false);

        for (var ordinal = 0; ordinal < authorization.Review.Items.Count; ordinal++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                history = await _historyStore.CompleteAsync(history.OperationId, FileRecycleActionTerminalState.Cancelled, CancellationToken.None).ConfigureAwait(false);
                return new FileRecycleExecutionResult(FileRecycleExecutionStatus.Cancelled, fresh, history, Failure: null);
            }

            var item = authorization.Review.Items[ordinal];
            history = await _historyStore.MarkMutationStartedAsync(history.OperationId, ordinal, CancellationToken.None).ConfigureAwait(false);
            var request = new FileRecycleMutationRequest(
                history.OperationId,
                authorization.AuthorizationId,
                ordinal,
                item.CanonicalPath,
                item.Identity);

            FileRecycleMutationResult mutation;
            try
            {
                mutation = await _mutationProvider.RecycleAsync(request).ConfigureAwait(false);
                if (mutation is null)
                {
                    throw new InvalidOperationException("Recycle provider returned no mutation result after the durable mutation barrier.");
                }
            }
            catch (Exception exception)
            {
                var failure = new FileOperationFailure(
                    "RecycleProviderThrewAfterMutationBarrier",
                    exception.Message,
                    item.CanonicalPath,
                    Retryable: false);
                history = await _historyStore.MarkRecoveryRequiredAsync(
                    history.OperationId,
                    ordinal,
                    item.Identity,
                    _providerName,
                    recycleLocator: null,
                    failure,
                    CancellationToken.None).ConfigureAwait(false);
                history = await _historyStore.CompleteAsync(history.OperationId, FileRecycleActionTerminalState.RecoveryRequired, CancellationToken.None).ConfigureAwait(false);
                return new FileRecycleExecutionResult(FileRecycleExecutionStatus.RecoveryRequired, fresh, history, failure);
            }

            if (!string.Equals(mutation.ProviderName, _providerName, StringComparison.Ordinal))
            {
                var failure = new FileOperationFailure(
                    "RecycleProviderNameMismatch",
                    "Recycle provider receipt did not name the provider instance bound to this executor.",
                    item.CanonicalPath,
                    Retryable: false);
                history = await _historyStore.MarkRecoveryRequiredAsync(
                    history.OperationId,
                    ordinal,
                    item.Identity,
                    _providerName,
                    mutation.RecycleLocator,
                    failure,
                    CancellationToken.None).ConfigureAwait(false);
                history = await _historyStore.CompleteAsync(history.OperationId, FileRecycleActionTerminalState.RecoveryRequired, CancellationToken.None).ConfigureAwait(false);
                return new FileRecycleExecutionResult(FileRecycleExecutionStatus.RecoveryRequired, fresh, history, failure);
            }

            if (mutation.SourceIdentity != item.Identity)
            {
                var failure = new FileOperationFailure(
                    "RecycleProviderIdentityMismatch",
                    "Recycle provider evidence did not belong to the exact source identity behind the durable mutation barrier.",
                    item.CanonicalPath,
                    Retryable: false);
                history = await _historyStore.MarkRecoveryRequiredAsync(
                    history.OperationId,
                    ordinal,
                    item.Identity,
                    _providerName,
                    mutation.RecycleLocator,
                    failure,
                    CancellationToken.None).ConfigureAwait(false);
                history = await _historyStore.CompleteAsync(history.OperationId, FileRecycleActionTerminalState.RecoveryRequired, CancellationToken.None).ConfigureAwait(false);
                return new FileRecycleExecutionResult(FileRecycleExecutionStatus.RecoveryRequired, fresh, history, failure);
            }

            switch (mutation.Disposition)
            {
                case FileRecycleMutationDisposition.Recycled:
                    history = await _historyStore.CommitRecycledAsync(
                        history.OperationId,
                        ordinal,
                        item.Identity,
                        _providerName,
                        mutation.RecycleLocator!,
                        CancellationToken.None).ConfigureAwait(false);
                    break;

                case FileRecycleMutationDisposition.NotMutated:
                    history = await _historyStore.MarkFailedAsync(
                        history.OperationId,
                        ordinal,
                        item.Identity,
                        _providerName,
                        mutation.Failure!,
                        CancellationToken.None).ConfigureAwait(false);
                    history = await _historyStore.CompleteAsync(history.OperationId, FileRecycleActionTerminalState.Failed, CancellationToken.None).ConfigureAwait(false);
                    return new FileRecycleExecutionResult(FileRecycleExecutionStatus.Failed, fresh, history, mutation.Failure);

                case FileRecycleMutationDisposition.Ambiguous:
                    history = await _historyStore.MarkRecoveryRequiredAsync(
                        history.OperationId,
                        ordinal,
                        item.Identity,
                        _providerName,
                        mutation.RecycleLocator,
                        mutation.Failure!,
                        CancellationToken.None).ConfigureAwait(false);
                    history = await _historyStore.CompleteAsync(history.OperationId, FileRecycleActionTerminalState.RecoveryRequired, CancellationToken.None).ConfigureAwait(false);
                    return new FileRecycleExecutionResult(FileRecycleExecutionStatus.RecoveryRequired, fresh, history, mutation.Failure);

                default:
                {
                    var failure = new FileOperationFailure(
                        "RecycleProviderDispositionUnknown",
                        "Recycle provider returned an unknown mutation disposition after the durable mutation barrier.",
                        item.CanonicalPath,
                        Retryable: false);
                    history = await _historyStore.MarkRecoveryRequiredAsync(
                        history.OperationId,
                        ordinal,
                        item.Identity,
                        _providerName,
                        mutation.RecycleLocator,
                        failure,
                        CancellationToken.None).ConfigureAwait(false);
                    history = await _historyStore.CompleteAsync(history.OperationId, FileRecycleActionTerminalState.RecoveryRequired, CancellationToken.None).ConfigureAwait(false);
                    return new FileRecycleExecutionResult(FileRecycleExecutionStatus.RecoveryRequired, fresh, history, failure);
                }
            }
        }

        history = await _historyStore.CompleteAsync(history.OperationId, FileRecycleActionTerminalState.Succeeded, CancellationToken.None).ConfigureAwait(false);
        return new FileRecycleExecutionResult(FileRecycleExecutionStatus.Succeeded, fresh, history, Failure: null);
    }
}
