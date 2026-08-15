using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

[Flags]
public enum FileCrossVolumeMoveSourcePreflightBlocker
{
    None = 0,
    SourceUnsupportedAttributes = 1 << 0,
    SourceNamedDataStreams = 1 << 1,
    SourceExtendedAttributes = 1 << 2,
    SourceProtectedLocation = 1 << 3,
}

/// <summary>
/// Read-only source evidence collected before cross-volume Copy begins. This evidence is
/// deliberately non-authorizing: it exists only to avoid creating a predictable destination
/// duplicate for source semantics that the current cross-volume Move contract cannot carry.
/// The later destination-bound fidelity proofs remain mandatory because source state can
/// change after this observation.
/// </summary>
public sealed record FileCrossVolumeMoveSourcePreflightEvidence(
    FileBasicMetadataEvidence CurrentSourceBasicMetadata,
    int SourceNamedDataStreamCount,
    uint SourceExtendedAttributeSize);

public sealed record FileCrossVolumeMoveSourcePreflightClassification(
    bool CanStartCopy,
    IReadOnlyList<FileCrossVolumeMoveSourcePreflightBlocker> Blockers,
    string Summary);

public static class FileCrossVolumeMoveSourcePreflightClassifier
{
    private const uint FileAttributeReadOnly = 0x00000001u;
    private const uint FileAttributeNormal = 0x00000080u;
    private const uint AllowedOrdinaryFileAttributes =
        (FileBasicMetadataEvidence.StableCopiedAttributesMask & ~FileAttributeReadOnly) |
        FileAttributeNormal;

    public static FileCrossVolumeMoveSourcePreflightClassification Classify(
        FileCrossVolumeMoveSourcePreflightEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.CurrentSourceBasicMetadata);
        if (evidence.SourceNamedDataStreamCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evidence));
        }

        var blockers = new List<FileCrossVolumeMoveSourcePreflightBlocker>();
        if ((evidence.CurrentSourceBasicMetadata.FileAttributes & ~AllowedOrdinaryFileAttributes) != 0)
        {
            blockers.Add(FileCrossVolumeMoveSourcePreflightBlocker.SourceUnsupportedAttributes);
        }

        if (FileCrossVolumeMovePreservationPolicy.RequiresNoSourceNamedDataStreamsAtProof &&
            evidence.SourceNamedDataStreamCount != 0)
        {
            blockers.Add(FileCrossVolumeMoveSourcePreflightBlocker.SourceNamedDataStreams);
        }

        if (FileCrossVolumeMovePreservationPolicy.RequiresNoSourceExtendedAttributesAtProof &&
            evidence.SourceExtendedAttributeSize != 0)
        {
            blockers.Add(FileCrossVolumeMoveSourcePreflightBlocker.SourceExtendedAttributes);
        }

        if (blockers.Count == 0)
        {
            return new FileCrossVolumeMoveSourcePreflightClassification(
                CanStartCopy: true,
                Array.Empty<FileCrossVolumeMoveSourcePreflightBlocker>(),
                "The exact source currently satisfies the deterministic cross-volume Move source subset. This read-only observation grants no Copy or source-delete authority and does not replace later fidelity proof.");
        }

        return new FileCrossVolumeMoveSourcePreflightClassification(
            CanStartCopy: false,
            blockers.AsReadOnly(),
            "Cross-volume Move can be refused before durable history and destination Copy because the exact source currently has semantics outside the supported cross-volume subset. This observation grants no mutation authority.");
    }
}

/// <summary>
/// Identity-bound request for a read-only source capability observation. It intentionally
/// carries no destination object, durable barrier, Copy receipt or delete authorization.
/// </summary>
public sealed record FileCrossVolumeMoveSourcePreflightRequest
{
    public FileCrossVolumeMoveSourcePreflightRequest(
        Guid operationId,
        int ordinal,
        FileOperationEntry entry,
        string canonicalSourceDirectoryPath,
        FileIdentity sourceDirectoryIdentity,
        string canonicalSourcePath,
        FileIdentity sourceIdentity)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourcePath);
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        if (entry.IsDirectory)
        {
            throw new ArgumentException(
                "Cross-volume Move source preflight supports regular files only.",
                nameof(entry));
        }
        if (sourceIdentity.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Cross-volume Move source preflight requires the source entry and source root to remain on the same validated filesystem volume.");
        }

        OperationId = operationId;
        Ordinal = ordinal;
        Entry = entry;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        CanonicalSourcePath = canonicalSourcePath;
        SourceIdentity = sourceIdentity;
    }

    public Guid OperationId { get; }

    public int Ordinal { get; }

    public FileOperationEntry Entry { get; }

    public string CanonicalSourceDirectoryPath { get; }

    public FileIdentity SourceDirectoryIdentity { get; }

    public string CanonicalSourcePath { get; }

    public FileIdentity SourceIdentity { get; }

    public bool SourcePreflightAuthorizesMutation => false;
}

public interface IFileCrossVolumeMoveSourcePreflightProbe
{
    ValueTask<FileCrossVolumeMoveSourcePreflightClassification> ProbeAsync(
        FileCrossVolumeMoveSourcePreflightRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adds the same protected-location eligibility used by the final source-delete capability
/// boundary to the early source preflight. This is still non-authorizing and deliberately
/// does not replace the final policy re-evaluation when the destructive lease is acquired.
/// </summary>
public sealed class FileCrossVolumeMoveProtectedLocationPreflightProbe :
    IFileCrossVolumeMoveSourcePreflightProbe
{
    private readonly IFileCrossVolumeMoveSourcePreflightProbe _inner;
    private readonly IFileDeleteProtectedLocationPolicy _protectedLocationPolicy;

    public FileCrossVolumeMoveProtectedLocationPreflightProbe(
        IFileCrossVolumeMoveSourcePreflightProbe inner,
        IFileDeleteProtectedLocationPolicy protectedLocationPolicy)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _protectedLocationPolicy = protectedLocationPolicy ??
            throw new ArgumentNullException(nameof(protectedLocationPolicy));
    }

    public ValueTask<FileCrossVolumeMoveSourcePreflightClassification> ProbeAsync(
        FileCrossVolumeMoveSourcePreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var rootDecision = _protectedLocationPolicy.Evaluate(
            request.CanonicalSourceDirectoryPath);
        if (rootDecision.IsBlocked)
        {
            return ValueTask.FromResult(BlockProtectedLocation(
                "source directory",
                rootDecision));
        }

        var sourceDecision = _protectedLocationPolicy.Evaluate(request.CanonicalSourcePath);
        if (sourceDecision.IsBlocked)
        {
            return ValueTask.FromResult(BlockProtectedLocation(
                "source file",
                sourceDecision));
        }

        return _inner.ProbeAsync(request, cancellationToken);
    }

    private static FileCrossVolumeMoveSourcePreflightClassification BlockProtectedLocation(
        string description,
        FileDeleteProtectedLocationResult decision) =>
        new(
            CanStartCopy: false,
            new[] { FileCrossVolumeMoveSourcePreflightBlocker.SourceProtectedLocation },
            $"Cross-volume Move can be refused before durable history and destination Copy because the exact {description} is protected from later source deletion: {decision.Reason} The protected-location policy will still be re-evaluated at the final source-delete capability boundary.");
}

/// <summary>
/// Decorates execution-grade validation with a source-only, non-authorizing schema-v1
/// cross-volume capability probe. Deterministic unsupported source state is converted to
/// Blocked before the composite executor can begin durable history or Copy. This does not
/// remove any later fresh validation or source/destination fidelity checkpoint.
///
/// The schema-v1 composite engine currently requires unequal 32-bit volume serials. Equal
/// serials are therefore outside this preflight's eligibility and are returned untouched;
/// equality is not treated as proof that the roots are on one volume. Production's stronger
/// handle-bound volume-GUID relationship proof owns that distinction, while the composite
/// executor/store independently fail closed on equal-serial history.
/// </summary>
public sealed class FileCrossVolumeMovePreflightExecutionValidator :
    IFileOperationExecutionValidator
{
    private readonly IFileOperationExecutionValidator _inner;
    private readonly IFileCrossVolumeMoveSourcePreflightProbe _probe;

    public FileCrossVolumeMovePreflightExecutionValidator(
        IFileOperationExecutionValidator inner,
        IFileCrossVolumeMoveSourcePreflightProbe probe)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public async ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
        FileOperationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validation = await _inner.ValidateAsync(plan, cancellationToken).ConfigureAwait(false);
        if (plan.Kind != FileOperationKind.Move ||
            validation.Status != FileOperationExecutionValidationStatus.Ready ||
            validation.SourceDirectory.Identity is not FileIdentity sourceDirectoryIdentity ||
            validation.DestinationDirectory.Identity is not FileIdentity destinationDirectoryIdentity ||
            sourceDirectoryIdentity.VolumeSerialNumber ==
                destinationDirectoryIdentity.VolumeSerialNumber)
        {
            return validation;
        }

        var items = validation.Items.ToArray();
        var blocked = false;
        for (var ordinal = 0; ordinal < items.Length; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[ordinal];
            if (item.Decision != FileOperationExecutionValidationDecision.Ready)
            {
                continue;
            }
            if (item.Source.State != FileOperationCanonicalPathState.File ||
                item.Source.IsLeafReparsePoint ||
                item.Source.Identity is not FileIdentity sourceIdentity)
            {
                // Preserve the inner result. The consuming executor owns the ordinary
                // execution-grade fail-closed validation for malformed Ready evidence.
                continue;
            }

            var request = new FileCrossVolumeMoveSourcePreflightRequest(
                plan.Id,
                ordinal,
                item.Entry,
                validation.SourceDirectory.CanonicalPath,
                sourceDirectoryIdentity,
                item.Source.CanonicalPath,
                sourceIdentity);
            var classification = await _probe
                .ProbeAsync(request, cancellationToken)
                .ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(classification);
            if (classification.CanStartCopy)
            {
                continue;
            }

            blocked = true;
            items[ordinal] = item with
            {
                Decision = FileOperationExecutionValidationDecision.Blocked,
                Message = classification.Summary +
                    " Blocking evidence: " + string.Join(", ", classification.Blockers) + ".",
            };
        }

        if (!blocked)
        {
            return validation;
        }

        return new FileOperationExecutionValidationResult(
            validation.Plan,
            validation.SourceDirectory,
            validation.DestinationDirectory,
            items,
            FileOperationExecutionValidationStatus.Blocked,
            validation.ValidatedAtUtc,
            "Cross-volume Move source preflight blocked the exact source before durable history or destination Copy. Later fidelity checks remain mandatory for sources that pass or subsequently change.");
    }
}
