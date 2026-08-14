using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Adds the Windows namespace-capability and current product-readiness boundaries
/// required by Move on top of the ordinary canonical execution validator.
///
/// The current FileOp path/identity model compares ordinary Windows namespace paths
/// case-insensitively. A per-directory case-sensitive NTFS namespace (or inability to
/// query that capability) must therefore fail before durable Move history begins.
///
/// Cross-volume composite Move infrastructure exists behind this validator, but its
/// destructive production path is deliberately disabled until the reviewed ordinary-user
/// security-fidelity and final proof-to-mutation stability boundaries are complete.
/// This block happens before durable history and before destination Copy begins.
/// </summary>
public sealed class WindowsMoveOperationExecutionValidator : IFileOperationExecutionValidator
{
    internal const string CrossVolumeMoveDisabledSummary =
        "Cross-volume Move destructive execution is disabled pending the reviewed ordinary-user security-fidelity (#186) and final proof-to-mutation stability (#187) boundaries. No durable history, destination Copy, or source-delete mutation was created by this product-readiness refusal.";

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

        if (IsCrossVolume(validation))
        {
            return Block(validation, CrossVolumeMoveDisabledSummary);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSupportedMutationRoots(validation, cancellationToken);
            return validation;
        }
        catch (NotSupportedException exception)
        {
            return Block(
                validation,
                "Move execution validation blocked the current Windows namespace capability before durable mutation history: " +
                exception.Message +
                " No MutationStarted record or filesystem mutation was created by this capability refusal.");
        }
    }

    private static bool IsCrossVolume(FileOperationExecutionValidationResult validation)
    {
        if (validation.SourceDirectory.Identity is not FileIdentity sourceIdentity ||
            validation.DestinationDirectory.Identity is not FileIdentity destinationIdentity)
        {
            // The ordinary execution validator is responsible for requiring stable root
            // identities before CanBeginMutation becomes true. If that contract ever
            // changes, do not guess at volume relationship here: fail closed instead.
            throw new InvalidOperationException(
                "A mutation-ready Move validation must retain stable source and destination root identities before product-readiness classification.");
        }

        return sourceIdentity.VolumeSerialNumber != destinationIdentity.VolumeSerialNumber;
    }

    private static FileOperationExecutionValidationResult Block(
        FileOperationExecutionValidationResult validation,
        string summary) =>
        new(
            validation.Plan,
            validation.SourceDirectory,
            validation.DestinationDirectory,
            validation.Items,
            FileOperationExecutionValidationStatus.Blocked,
            DateTimeOffset.UtcNow,
            summary);

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