using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

/// <summary>
/// Provider-dispatch guarded entry point for concrete final-lease implementations that must reject
/// remembered/replayed Core requests outside the exact preparation callback that minted them.
/// </summary>
public static class FileDeleteOperationFinalMutationLeaseDispatchPreparation
{
    public static ValueTask<FileDeleteOperationFinalMutationLeaseScope> AcquireAsync(
        FileDeleteOperationPreMutationPreparationScope readOnlyPreparationScope,
        IFileDeleteOperationFinalMutationLeaseProvider finalLeaseProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readOnlyPreparationScope);
        ArgumentNullException.ThrowIfNull(finalLeaseProvider);

        return FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
            readOnlyPreparationScope,
            new DispatchingProvider(finalLeaseProvider),
            cancellationToken);
    }

    private sealed class DispatchingProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        private readonly IFileDeleteOperationFinalMutationLeaseProvider _inner;

        public DispatchingProvider(IFileDeleteOperationFinalMutationLeaseProvider inner)
        {
            _inner = inner;
        }

        public async ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            using (FileDeleteOperationFinalMutationLeaseProviderDispatch.Begin(request))
            {
                return await _inner
                    .AcquireAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
