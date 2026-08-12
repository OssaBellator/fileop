using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

/// <summary>
/// Preserves cleanup-only ownership when a detached final delete capability could not be
/// released on a failed mutation-barrier path. It exposes no lease, handle, request, mutation
/// method, or mutation authority; callers can only retry release.
/// </summary>
public sealed class FileDeleteOperationFinalLeaseReleaseException : Exception, IAsyncDisposable
{
    private readonly SemaphoreSlim _releaseGate = new(1, 1);
    private IFileDeleteOperationFinalMutationLease? _lease;

    internal FileDeleteOperationFinalLeaseReleaseException(
        string message,
        IEnumerable<Exception> causes,
        IFileDeleteOperationFinalMutationLease lease)
        : base(message, CreateInnerException(causes))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(lease);
        _lease = lease;
    }

    /// <summary>
    /// True while this exception still privately owns a final lease whose release has not
    /// completed successfully.
    /// </summary>
    public bool FinalLeaseReleasePending => Volatile.Read(ref _lease) is not null;

    /// <summary>
    /// A cleanup failure never grants mutation authority, even if durable history is recovery-sensitive.
    /// </summary>
    public bool DeleteMutationAuthorized => false;

    public async ValueTask RetryFinalLeaseReleaseAsync()
    {
        await _releaseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lease = Volatile.Read(ref _lease);
            if (lease is null)
            {
                return;
            }

            await lease.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _lease, null);
        }
        finally
        {
            _releaseGate.Release();
        }
    }

    public ValueTask DisposeAsync() => RetryFinalLeaseReleaseAsync();

    private static Exception CreateInnerException(IEnumerable<Exception> causes)
    {
        ArgumentNullException.ThrowIfNull(causes);
        var snapshot = causes.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "A final lease release failure must retain at least one causal exception.",
                nameof(causes));
        }

        foreach (var cause in snapshot)
        {
            ArgumentNullException.ThrowIfNull(cause);
        }

        return snapshot.Length == 1
            ? snapshot[0]
            : new AggregateException(snapshot);
    }
}
