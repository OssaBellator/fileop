using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Production Move composition for the current 64-bit mutation FileIdentity model. The
/// ordinary Windows Move validator retains its namespace and volume-relationship policy;
/// only a mutation-ready result is then admitted through the handle-bound NTFS guard.
/// </summary>
public sealed class WindowsNtfsMoveOperationExecutionValidator :
    IFileOperationExecutionValidator
{
    private readonly WindowsNtfsMutationExecutionValidator _inner;

    public WindowsNtfsMoveOperationExecutionValidator(
        IFileOperationExecutionValidator? moveValidator = null,
        IWindowsMutationFilesystemCapabilityProbe? mutationFilesystemProbe = null)
    {
        _inner = new WindowsNtfsMutationExecutionValidator(
            moveValidator ?? new WindowsMoveOperationExecutionValidator(),
            mutationFilesystemProbe);
    }

    public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return _inner.ValidateAsync(plan, cancellationToken);
    }
}
