using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Adds the Windows namespace-capability boundary required by the current Move
/// implementation on top of the ordinary canonical execution validator.
///
/// The current FileOp path/identity model compares ordinary Windows namespace
/// paths case-insensitively. A per-directory case-sensitive NTFS namespace (or
/// an inability to query that capability) must therefore fail before durable
/// Move history begins, and must fail again during the executor's fresh
/// per-entry validation immediately before MutationStarted.
/// </summary>
public sealed class WindowsMoveOperationExecutionValidator : IFileOperationExecutionValidator
{
    private readonly IFileOperationExecutionValidator _inner;
    private readonly IFileOperationNamespaceCapabilityProbe _namespaceProbe;

    public WindowsMoveOperationExecutionValidator(
        IFileOperationExecutionValidator? inner = null,
        IFileOperationNamespaceCapabilityProbe? namespaceProbe = null)
    {
        _inner = inner ?? new WindowsFileOperationExecutionValidator();
        _namespaceProbe = namespaceProbe ?? new WindowsFileOperationNamespaceCapabilityProbe();
    }

    public async ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validation = await _inner.ValidateAsync(plan, cancellationToken).ConfigureAwait(false);
        if (plan.Kind != FileOperationKind.Move || !validation.CanBeginMutation)
        {
            return validation;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSupportedMutationRoots(validation, cancellationToken);
            return validation;
        }
        catch (NotSupportedException exception)
        {
            return new FileOperationExecutionValidationResult(
                validation.Plan,
                validation.SourceDirectory,
                validation.DestinationDirectory,
                validation.Items,
                FileOperationExecutionValidationStatus.Blocked,
                DateTimeOffset.UtcNow,
                "Move execution validation blocked the current Windows namespace capability before durable mutation history: " +
                exception.Message +
                " No MutationStarted record or filesystem mutation was created by this capability refusal.");
        }
    }

    private void RequireSupportedMutationRoots(
        FileOperationExecutionValidationResult validation,
        CancellationToken cancellationToken)
    {
        foreach (var path in new[]
        {
            validation.SourceDirectory.CanonicalPath,
            validation.DestinationDirectory.CanonicalPath,
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capability = _namespaceProbe.QueryDirectory(path);
            if (!capability.CanUseCurrentMutationModel)
            {
                throw new NotSupportedException(
                    $"Namespace capability for '{capability.CanonicalDirectoryPath}' is {capability.State}: {capability.Summary}");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
