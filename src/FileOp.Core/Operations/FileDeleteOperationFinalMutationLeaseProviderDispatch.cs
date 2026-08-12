using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FileOp.Core.Operations;

/// <summary>
/// Tracks the short Core-owned provider-dispatch window for one final-lease request.
/// The public request remains non-authorizing; FileOp.Windows can only observe whether
/// Core is currently dispatching that exact request through the reviewed coordinator.
/// </summary>
internal static class FileDeleteOperationFinalMutationLeaseProviderDispatch
{
    private static readonly ConditionalWeakTable<
        FileDeleteOperationFinalMutationLeaseRequest,
        DispatchState> States = new();

    internal static IDisposable Begin(FileDeleteOperationFinalMutationLeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = States.GetOrCreateValue(request);
        if (Interlocked.CompareExchange(ref state.Active, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "The final delete lease request is already inside a provider dispatch window.");
        }

        return new DispatchScope(state);
    }

    internal static bool IsActive(FileDeleteOperationFinalMutationLeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return States.TryGetValue(request, out var state) &&
            Volatile.Read(ref state.Active) == 1;
    }

    private sealed class DispatchState
    {
        public int Active;
    }

    private sealed class DispatchScope : IDisposable
    {
        private DispatchState? _state;

        public DispatchScope(DispatchState state)
        {
            _state = state;
        }

        public void Dispose()
        {
            var state = Interlocked.Exchange(ref _state, null);
            if (state is not null)
            {
                Volatile.Write(ref state.Active, 0);
            }
        }
    }
}
