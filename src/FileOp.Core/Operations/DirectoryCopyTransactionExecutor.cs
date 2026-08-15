using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public sealed record DirectoryCopyMutationRequest(
    DirectoryCopyActionEntry Action,
    string CanonicalDestinationParentPath,
    FileIdentity DestinationParentIdentity);

public sealed record DirectoryCopyMutationReceipt(
    int Ordinal,
    DirectoryCopyActionKind Kind,
    string CanonicalSourcePath,
    FileIdentity SourceIdentity,
    string CanonicalDestinationPath,
    FileIdentity DestinationIdentity,
    FileContentFingerprint? DestinationContentFingerprint = null);

/// <summary>
/// Keeps the exact source/destination handles needed by a future Windows recursive Copy
/// primitive alive through durable history commit. Disposing the lease grants no cleanup,
/// rollback or delete authority.
/// </summary>
public interface IDirectoryCopyMutationLease : IAsyncDisposable
{
    DirectoryCopyMutationReceipt Receipt { get; }
}

public interface IDirectoryCopyMutationPrimitive
{
    /// <summary>
    /// Performs exactly one no-replace action. The implementation must identity-bind the
    /// supplied destination parent and source object. No cancellation token is accepted
    /// because callers cross a durable MutationStarted barrier first.
    /// </summary>
    ValueTask<IDirectoryCopyMutationLease> ExecuteNoReplaceAsync(
        DirectoryCopyMutationRequest request);
}

/// <summary>
/// Dormant recursive directory-Copy transaction engine. Product wiring remains disabled until
/// a reviewed Windows fresh-manifest/fidelity acquirer and identity-bound mutation primitive
/// exist. This executor never enumerates paths or substitutes path-only filesystem APIs.
/// </summary>
public sealed class DirectoryCopyTransactionExecutor
{
    private readonly DirectoryCopyFreshManifestGate _freshGate;
    private readonly IDirectoryCopyActionHistoryStore _historyStore;
    private readonly IDirectoryCopyMutationPrimitive _mutation;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _activeLock = new();
    private ActiveExecution? _active;

    public DirectoryCopyTransactionExecutor(
        DirectoryCopyFreshManifestGate freshGate,
        IDirectoryCopyActionHistoryStore historyStore,
        IDirectoryCopyMutationPrimitive mutation,
        TimeProvider? timeProvider = null)
    {
        _freshGate = freshGate ?? throw new ArgumentNullException(nameof(freshGate));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _mutation = mutation ?? throw new ArgumentNullException(nameof(mutation));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<DirectoryCopyActionHistory> ExecuteAsync(
        DirectoryCopyTransactionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var context = new ActiveExecution(plan.OperationId);
        lock (_activeLock) _active = context;
        try
        {
            return await ExecuteCoreAsync(plan, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_activeLock)
            {
                if (ReferenceEquals(_active, context)) _active = null;
            }
            context.Dispose();
            _executionGate.Release();
        }
    }

    public ValueTask<bool> RequestCancellationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_activeLock)
        {
            if (_active is null || _active.OperationId != operationId)
            {
                return ValueTask.FromResult(false);
            }
            _active.RequestCancellation();
            return ValueTask.FromResult(true);
        }
    }

    private async ValueTask<DirectoryCopyActionHistory> ExecuteCoreAsync(
        DirectoryCopyTransactionPlan plan,
        ActiveExecution context,
        CancellationToken callerCancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            context.SafeBoundaryCancellation.Token);
        var fresh = await _freshGate
            .PrepareAsync(plan.ReviewedManifest, linked.Token)
            .ConfigureAwait(false);

        if (!fresh.CanBeginDurableHistory)
        {
            throw new InvalidOperationException(
                "Directory Copy cannot begin durable history: " + fresh.Summary);
        }
        if (context.IsCancellationRequested || callerCancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(linked.Token);
        }

        DirectoryCopyActionHistory history = await _historyStore
            .BeginAsync(plan, fresh, UtcNow(), cancellationToken: linked.Token)
            .ConfigureAwait(false);

        for (var ordinal = 0; ordinal < history.Entries.Count; ordinal++)
        {
            if (context.IsCancellationRequested || callerCancellation.IsCancellationRequested)
            {
                return await _historyStore
                    .CompleteAsync(
                        plan.OperationId,
                        DirectoryCopyActionTerminalState.Cancelled,
                        UtcNow(),
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var action = history.Entries[ordinal];
            if (!TryResolveCommittedParent(plan, history, action, out var parentPath, out var parentIdentity, out var parentProblem))
            {
                var failure = new FileOperationFailure(
                    "DirectoryCopyParentIdentityUnavailable",
                    parentProblem,
                    action.CanonicalDestinationPath,
                    Retryable: true);
                history = await _historyStore
                    .MarkFailedBeforeMutationAsync(
                        plan.OperationId,
                        ordinal,
                        failure,
                        UtcNow(),
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                return await _historyStore
                    .CompleteAsync(
                        plan.OperationId,
                        DirectoryCopyActionTerminalState.Failed,
                        UtcNow(),
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }

            try
            {
                history = await _historyStore
                    .MarkMutationStartedAsync(
                        plan.OperationId,
                        ordinal,
                        UtcNow(),
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                await BestEffortCompleteAsync(plan.OperationId, DirectoryCopyActionTerminalState.Failed)
                    .ConfigureAwait(false);
                throw;
            }

            IDirectoryCopyMutationLease? lease = null;
            DirectoryCopyMutationReceipt? receipt = null;
            try
            {
                // No cancellation token crosses the durable MutationStarted boundary.
                lease = await _mutation
                    .ExecuteNoReplaceAsync(new DirectoryCopyMutationRequest(
                        action,
                        parentPath,
                        parentIdentity))
                    .ConfigureAwait(false);
                if (lease is null)
                {
                    throw new InvalidOperationException("Directory Copy mutation provider returned no lease.");
                }
                receipt = lease.Receipt;
                ValidateReceipt(action, parentIdentity, receipt);
                history = await _historyStore
                    .CommitAsync(
                        plan.OperationId,
                        ordinal,
                        receipt.DestinationIdentity,
                        receipt.DestinationContentFingerprint,
                        UtcNow(),
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                FileIdentity? observedIdentity = receipt?.DestinationIdentity;
                FileContentFingerprint? observedFingerprint = receipt?.DestinationContentFingerprint;
                var failure = FailureFromException(
                    "DirectoryCopyMutationAmbiguous",
                    "A recursive directory Copy action crossed MutationStarted but did not reach durable commit. Recovery inspection is required.",
                    action.CanonicalDestinationPath,
                    exception);
                try
                {
                    history = await _historyStore
                        .MarkRecoveryRequiredAsync(
                            plan.OperationId,
                            ordinal,
                            failure,
                            UtcNow(),
                            observedIdentity,
                            observedFingerprint,
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                    history = await _historyStore
                        .CompleteAsync(
                            plan.OperationId,
                            DirectoryCopyActionTerminalState.RecoveryRequired,
                            UtcNow(),
                            cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
                }
                return history;
            }

            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        return await _historyStore
            .CompleteAsync(
                plan.OperationId,
                DirectoryCopyActionTerminalState.Succeeded,
                UtcNow(),
                cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static bool TryResolveCommittedParent(
        DirectoryCopyTransactionPlan plan,
        DirectoryCopyActionHistory history,
        DirectoryCopyActionEntry action,
        out string canonicalParentPath,
        out FileIdentity parentIdentity,
        out string problem)
    {
        if (action.DestinationParentOrdinal is null)
        {
            canonicalParentPath = plan.DestinationParent.CanonicalPath;
            parentIdentity = plan.DestinationParent.Identity!.Value;
            problem = string.Empty;
            return true;
        }

        var parent = history.Entries[action.DestinationParentOrdinal.Value];
        if (parent.Kind != DirectoryCopyActionKind.CreateDirectory ||
            parent.State != DirectoryCopyActionEntryState.Committed ||
            parent.DestinationIdentity is not FileIdentity committedIdentity)
        {
            canonicalParentPath = string.Empty;
            parentIdentity = default;
            problem =
                $"Destination parent action {action.DestinationParentOrdinal.Value} is not a durably committed directory identity.";
            return false;
        }

        canonicalParentPath = parent.CanonicalDestinationPath;
        parentIdentity = committedIdentity;
        problem = string.Empty;
        return true;
    }

    private static void ValidateReceipt(
        DirectoryCopyActionEntry action,
        FileIdentity parentIdentity,
        DirectoryCopyMutationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Ordinal != action.Ordinal ||
            receipt.Kind != action.Kind ||
            !PathEquals(receipt.CanonicalSourcePath, action.CanonicalSourcePath) ||
            receipt.SourceIdentity != action.SourceIdentity ||
            !PathEquals(receipt.CanonicalDestinationPath, action.CanonicalDestinationPath))
        {
            throw new InvalidOperationException(
                "Directory Copy mutation receipt does not match the exact durable action/source/destination binding.");
        }
        if (receipt.DestinationIdentity.VolumeSerialNumber != parentIdentity.VolumeSerialNumber)
        {
            throw new InvalidOperationException(
                "Directory Copy destination identity is not on the durably bound destination-parent volume.");
        }
        if (action.Kind == DirectoryCopyActionKind.CopyFile && receipt.DestinationContentFingerprint is null)
        {
            throw new InvalidOperationException(
                "Directory Copy file commit requires destination SHA-256 content evidence from the mutation provider.");
        }
        if (action.Kind == DirectoryCopyActionKind.CreateDirectory && receipt.DestinationContentFingerprint is not null)
        {
            throw new InvalidOperationException(
                "Directory creation must not publish file-content fingerprint evidence.");
        }
    }

    private async ValueTask BestEffortCompleteAsync(
        Guid operationId,
        DirectoryCopyActionTerminalState terminalState)
    {
        try
        {
            await _historyStore.CompleteAsync(operationId, terminalState, UtcNow()).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static FileOperationFailure FailureFromException(
        string code,
        string message,
        string path,
        Exception exception) =>
        new(
            code,
            message + " " + exception.Message,
            path,
            Retryable: true);

    private sealed class ActiveExecution : IDisposable
    {
        public ActiveExecution(Guid operationId)
        {
            OperationId = operationId;
        }

        public Guid OperationId { get; }
        public CancellationTokenSource SafeBoundaryCancellation { get; } = new();
        public bool IsCancellationRequested => SafeBoundaryCancellation.IsCancellationRequested;
        public void RequestCancellation()
        {
            if (!SafeBoundaryCancellation.IsCancellationRequested) SafeBoundaryCancellation.Cancel();
        }
        public void Dispose() => SafeBoundaryCancellation.Dispose();
    }
}
