using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Adds the Windows namespace-capability and volume-relationship boundaries required by
/// the current same-volume Move implementation on top of ordinary canonical execution
/// validation.
///
/// The current FileOp path/identity model compares ordinary Windows namespace paths
/// case-insensitively. A per-directory case-sensitive NTFS namespace (or an inability to
/// query that capability) must therefore fail before durable Move history begins.
///
/// A 32-bit filesystem volume serial is not treated as collision-free proof that two roots
/// belong to the same filesystem volume. Different serials remain sufficient evidence that
/// the roots differ and are left for the existing Move strategy/product block to classify.
/// Equal serials require a stronger handle-bound Windows volume-GUID relationship bound to
/// the exact freshly validated root identities before same-volume rename can remain ready.
/// </summary>
public sealed class WindowsMoveOperationExecutionValidator : IFileOperationExecutionValidator
{
    private const string MissingRootIdentitySummary =
        "Move execution validation did not retain stable source and destination root filesystem identities required for safe volume classification. No durable mutation history or filesystem mutation was created.";

    private const string EqualSerialDifferentVolumeSummary =
        "Move roots have equal 32-bit volume-serial evidence but resolve to different handle-bound Windows volume GUIDs. They must not enter the same-volume rename path. Cross-volume Move remains unsupported by the current product route, so no durable mutation history or filesystem mutation was created.";

    private readonly IFileOperationExecutionValidator _inner;
    private readonly IFileOperationNamespaceCapabilityProbe _namespaceProbe;
    private readonly IFileOperationVolumeRelationshipProbe _volumeRelationshipProbe;

    public WindowsMoveOperationExecutionValidator(
        IFileOperationExecutionValidator? inner = null,
        IFileOperationNamespaceCapabilityProbe? namespaceProbe = null,
        IFileOperationVolumeRelationshipProbe? volumeRelationshipProbe = null)
    {
        _inner = inner ?? new WindowsFileOperationExecutionValidator();
        _namespaceProbe = namespaceProbe ?? new WindowsFileOperationNamespaceCapabilityProbe();
        _volumeRelationshipProbe = volumeRelationshipProbe ??
            new WindowsFileOperationVolumeRelationshipProbe();
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

        if (validation.SourceDirectory.Identity is not FileIdentity sourceIdentity ||
            validation.DestinationDirectory.Identity is not FileIdentity destinationIdentity)
        {
            return Block(validation, MissingRootIdentitySummary);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceIdentity.VolumeSerialNumber == destinationIdentity.VolumeSerialNumber)
            {
                var relationship = _volumeRelationshipProbe.Query(
                    validation.SourceDirectory.CanonicalPath,
                    sourceIdentity,
                    validation.DestinationDirectory.CanonicalPath,
                    destinationIdentity);
                switch (relationship.State)
                {
                    case FileOperationVolumeRelationshipState.SameVolume:
                        break;

                    case FileOperationVolumeRelationshipState.DifferentVolume:
                        return Block(validation, EqualSerialDifferentVolumeSummary);

                    default:
                        throw new NotSupportedException(
                            "Move roots have equal volume-serial evidence, but Windows could not prove that their exact validated identities belong to the same filesystem volume. " +
                            relationship.Summary);
                }
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
