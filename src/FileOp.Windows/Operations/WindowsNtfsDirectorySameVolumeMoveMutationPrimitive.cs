using System;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Repeats both mutation identity boundaries at the raw directory rename provider:
/// exact handle-bound NTFS proof (#193) and exact stronger same-volume relationship
/// proof (#192). Product validation remains the normal earlier refusal path.
/// </summary>
public sealed class WindowsNtfsDirectorySameVolumeMoveMutationPrimitive :
    IDirectorySameVolumeMoveMutationPrimitive
{
    private readonly IDirectorySameVolumeMoveMutationPrimitive _inner;
    private readonly IWindowsMutationFilesystemCapabilityProbe _filesystemProbe;
    private readonly IFileOperationVolumeRelationshipProbe _volumeRelationshipProbe;

    public WindowsNtfsDirectorySameVolumeMoveMutationPrimitive(
        IDirectorySameVolumeMoveMutationPrimitive? inner = null,
        IWindowsMutationFilesystemCapabilityProbe? filesystemProbe = null,
        IFileOperationVolumeRelationshipProbe? volumeRelationshipProbe = null)
    {
        _inner = inner ?? new WindowsDirectorySameVolumeMoveMutationPrimitive();
        _filesystemProbe = filesystemProbe ?? new WindowsMutationFilesystemCapabilityProbe();
        _volumeRelationshipProbe = volumeRelationshipProbe ?? new WindowsFileOperationVolumeRelationshipProbe();
    }

    public ValueTask<IDirectorySameVolumeMoveMutationLease> RenameDirectoryAsync(
        DirectorySameVolumeMoveMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceRootIdentity = request.SourceDirectory.Identity
            ?? throw new InvalidOperationException(
                "Directory Move mutation requires fresh source-root identity evidence before NTFS/volume proof.");
        var destinationRootIdentity = request.DestinationDirectory.Identity
            ?? throw new InvalidOperationException(
                "Directory Move mutation requires fresh destination-root identity evidence before NTFS/volume proof.");

        WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(
            _filesystemProbe,
            request.SourceDirectory.CanonicalPath,
            sourceRootIdentity,
            "Directory Move source mutation root");
        WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(
            _filesystemProbe,
            request.DestinationDirectory.CanonicalPath,
            destinationRootIdentity,
            "Directory Move destination mutation root");

        if (sourceRootIdentity.VolumeSerialNumber != destinationRootIdentity.VolumeSerialNumber)
        {
            throw new NotSupportedException(
                "Same-volume directory Move requires equal validated root serial evidence before the stronger volume proof.");
        }

        var relationship = _volumeRelationshipProbe.Query(
            request.SourceDirectory.CanonicalPath,
            sourceRootIdentity,
            request.DestinationDirectory.CanonicalPath,
            destinationRootIdentity);
        if (relationship is null ||
            relationship.State != FileOperationVolumeRelationshipState.SameVolume)
        {
            throw new NotSupportedException(
                "Directory Move requires exact handle-bound proof that both validated roots remain on the same Windows volume. " +
                (relationship?.Summary ?? "The volume relationship provider returned no evidence."));
        }

        return _inner.RenameDirectoryAsync(request);
    }
}
