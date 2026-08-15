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
/// Cross-volume composite Move infrastructure exists behind this validator. Its security
/// contract follows Windows destination-default/inherited semantics and its preservation
/// contract is deliberately scoped to the selected source directory entry, stable main
/// stream and observed unsupported source ADS/EA state rather than a fictional global NTFS
/// metadata transaction. Destructive production execution remains disabled until that exact
/// contract passes its native Windows validation matrix. This block happens before durable
/// history and before destination Copy begins.
///
/// A 32-bit filesystem volume serial is not treated as collision-free. Different serials
/// prove different volumes directly; equal serials require a stronger handle-bound Windows
/// volume-GUID relationship bound to the exact freshly validated root identities before
/// same-volume rename can remain mutation-ready.
/// </summary>
public sealed class WindowsMoveOperationExecutionValidator : IFileOperationExecutionValidator
{
    // Repository tracking: #186 selected the ordinary-user destination-default security
    // contract source-side. #187 remains the proof-to-mutation stability blocker.
    // Keep issue IDs out of runtime UI text.
    internal const string CrossVolumeMoveDisabledSummary =
        "Cross-volume Move is currently disabled while its final mutation-stability boundary is still under review. Choose a destination on the same volume to use the supported Move path. No durable history, destination Copy, or source-delete mutation was created by this refusal.";

    private const string MissingRootIdentitySummary =
        "Move execution validation did not retain stable source and destination root filesystem identities required for mutation classification. No durable mutation history or filesystem mutation was created.";

    private readonly IFileOperationExecutionValidator _inner;
    private readonly IFileOperationNamespaceCapabilityProbe _namespaceProbe;
    private readonly IFileOperationVolumeRelationshipProbe _volumeRelationshipProbe;

    public WindowsMoveOperationExecutionValidator(
        IFileOperationExecutionValidator? inner = null,
        IFileOperationNamespaceCapabilityProbe? namespaceProbe = null)
    {
        _inner = inner ?? new WindowsFileOperationExecutionValidator();
        _namespaceProbe = namespaceProbe ?? new WindowsFileOperationNamespaceCapabilityProbe();
        _volumeRelationshipProbe = new WindowsFileOperationVolumeRelationshipProbe();
    }

    internal WindowsMoveOperationExecutionValidator(
        IFileOperationExecutionValidator inner,
        IFileOperationNamespaceCapabilityProbe namespaceProbe,
        IFileOperationVolumeRelationshipProbe volumeRelationshipProbe)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _namespaceProbe = namespaceProbe ?? throw new ArgumentNullException(nameof(namespaceProbe));
        _volumeRelationshipProbe = volumeRelationshipProbe ??
            throw new ArgumentNullException(nameof(volumeRelationshipProbe));
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

        if (validation.SourceDirectory.Identity is not FileIdentity ||
            validation.DestinationDirectory.Identity is not FileIdentity)
        {
            return Block(validation, MissingRootIdentitySummary);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryClassifyVolumeRelationship(validation, out var isCrossVolume))
            {
                return Block(validation, MissingRootIdentitySummary);
            }

            if (isCrossVolume)
            {
                return Block(validation, CrossVolumeMoveDisabledSummary);
            }

            cancellationToken.ThrowIfCancellationRequested();
            RequireSupportedMutationRoots(validation, cancellationToken);
            return validation;
        }
        catch (NotSupportedException exception)
        {
            return Block(
                validation,
                "Move execution validation blocked a required Windows mutation capability before durable mutation history: " +
                exception.Message +
                " No MutationStarted record or filesystem mutation was created by this capability refusal.");
        }
    }

    private bool TryClassifyVolumeRelationship(
        FileOperationExecutionValidationResult validation,
        out bool isCrossVolume)
    {
        if (validation.SourceDirectory.Identity is not FileIdentity sourceIdentity ||
            validation.DestinationDirectory.Identity is not FileIdentity destinationIdentity)
        {
            isCrossVolume = false;
            return false;
        }

        if (sourceIdentity.VolumeSerialNumber != destinationIdentity.VolumeSerialNumber)
        {
            isCrossVolume = true;
            return true;
        }

        var relationship = _volumeRelationshipProbe.Query(
            validation.SourceDirectory.CanonicalPath,
            sourceIdentity,
            validation.DestinationDirectory.CanonicalPath,
            destinationIdentity);
        switch (relationship.State)
        {
            case FileOperationVolumeRelationshipState.SameVolume:
                isCrossVolume = false;
                return true;

            case FileOperationVolumeRelationshipState.DifferentVolume:
                isCrossVolume = true;
                return true;

            default:
                throw new NotSupportedException(
                    "Move roots have equal volume-serial evidence, but Windows could not prove that their exact validated identities belong to the same filesystem volume. " +
                    relationship.Summary);
        }
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
