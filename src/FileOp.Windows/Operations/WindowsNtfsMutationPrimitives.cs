using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Repeats the #193 filesystem-identity boundary at the final Windows mutation-provider
/// acquisition point. Execution validation remains the normal early refusal path; these
/// wrappers are defense-in-depth so a future product-composition mistake cannot directly
/// invoke the reviewed raw primitives on an unproven filesystem.
/// </summary>
internal static class WindowsNtfsMutationCapabilityGuard
{
    public static void RequireExactNtfs(
        IWindowsMutationFilesystemCapabilityProbe probe,
        string canonicalDirectoryPath,
        FileIdentity expectedIdentity,
        string description)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var capability = probe.QueryDirectory(canonicalDirectoryPath, expectedIdentity);
        if (capability is null ||
            !capability.CanUseCurrentMutationIdentity ||
            !capability.IsBoundTo(canonicalDirectoryPath, expectedIdentity))
        {
            throw new NotSupportedException(
                $"{description} requires exact handle-bound NTFS filesystem evidence for the current mutation FileIdentity model. " +
                (capability?.Summary ?? "The filesystem capability provider returned no evidence."));
        }
    }
}

public sealed class WindowsNtfsFileCopyMutationPrimitive : IFileCopyMutationPrimitive
{
    private readonly IFileCopyMutationPrimitive _inner;
    private readonly IWindowsMutationFilesystemCapabilityProbe _probe;

    public WindowsNtfsFileCopyMutationPrimitive(
        IFileCopyMutationPrimitive? inner = null,
        IWindowsMutationFilesystemCapabilityProbe? probe = null)
    {
        _inner = inner ?? new WindowsFileCopyMutationPrimitive();
        _probe = probe ?? new WindowsMutationFilesystemCapabilityProbe();
    }

    public ValueTask<IFileCopyMutationLease> CopyNewFileAsync(FileCopyMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceIdentity = request.SourceDirectory.Identity
            ?? throw new InvalidOperationException(
                "Copy mutation requires fresh source-root identity evidence before NTFS capability proof.");
        var destinationIdentity = request.DestinationDirectory.Identity
            ?? throw new InvalidOperationException(
                "Copy mutation requires fresh destination-root identity evidence before NTFS capability proof.");

        WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(
            _probe,
            request.SourceDirectory.CanonicalPath,
            sourceIdentity,
            "Copy source mutation root");
        WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(
            _probe,
            request.DestinationDirectory.CanonicalPath,
            destinationIdentity,
            "Copy destination mutation root");

        return _inner.CopyNewFileAsync(request);
    }
}

public sealed class WindowsNtfsFileSameVolumeMoveMutationPrimitive :
    IFileSameVolumeMoveMutationPrimitive
{
    private readonly IFileSameVolumeMoveMutationPrimitive _inner;
    private readonly IWindowsMutationFilesystemCapabilityProbe _probe;

    public WindowsNtfsFileSameVolumeMoveMutationPrimitive(
        IFileSameVolumeMoveMutationPrimitive? inner = null,
        IWindowsMutationFilesystemCapabilityProbe? probe = null)
    {
        _inner = inner ?? new WindowsFileSameVolumeMoveMutationPrimitive();
        _probe = probe ?? new WindowsMutationFilesystemCapabilityProbe();
    }

    public ValueTask<IFileSameVolumeMoveMutationLease> RenameFileAsync(
        FileSameVolumeMoveMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceIdentity = request.SourceDirectory.Identity
            ?? throw new InvalidOperationException(
                "Move mutation requires fresh source-root identity evidence before NTFS capability proof.");
        var destinationIdentity = request.DestinationDirectory.Identity
            ?? throw new InvalidOperationException(
                "Move mutation requires fresh destination-root identity evidence before NTFS capability proof.");

        WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(
            _probe,
            request.SourceDirectory.CanonicalPath,
            sourceIdentity,
            "Move source mutation root");
        WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(
            _probe,
            request.DestinationDirectory.CanonicalPath,
            destinationIdentity,
            "Move destination mutation root");

        return _inner.RenameFileAsync(request);
    }
}

public sealed class WindowsNtfsFileDeleteOperationFinalMutationLeaseProvider :
    IFileDeleteOperationFinalMutationLeaseProvider
{
    private readonly IFileDeleteOperationFinalMutationLeaseProvider _inner;
    private readonly IWindowsMutationFilesystemCapabilityProbe _probe;

    public WindowsNtfsFileDeleteOperationFinalMutationLeaseProvider(
        IFileDeleteOperationFinalMutationLeaseProvider? inner = null,
        IWindowsMutationFilesystemCapabilityProbe? probe = null)
    {
        _inner = inner ?? new WindowsFileDeleteOperationFinalMutationLeaseProvider();
        _probe = probe ?? new WindowsMutationFilesystemCapabilityProbe();
    }

    public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
        FileDeleteOperationFinalMutationLeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(
            _probe,
            request.Authorization.CanonicalSourceDirectoryPath,
            request.Authorization.SourceDirectoryIdentity,
            "Delete final mutation root");

        cancellationToken.ThrowIfCancellationRequested();
        return _inner.AcquireAsync(request, cancellationToken);
    }
}
