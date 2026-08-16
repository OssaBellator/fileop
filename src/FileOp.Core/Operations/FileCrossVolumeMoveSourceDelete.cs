using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// Pre-barrier request for the destructive half of one cross-volume Move entry.
/// The destination has already reached a durable content-bound commit, but this request
/// deliberately carries no source-delete authority. A provider must first reacquire and
/// identity-bind the original source under a live DELETE-capable handle lease.
/// </summary>
public sealed record FileCrossVolumeMoveSourceDeleteRequest
{
    public FileCrossVolumeMoveSourceDeleteRequest(
        Guid operationId,
        int ordinal,
        FileOperationEntry entry,
        string canonicalSourceDirectoryPath,
        FileIdentity sourceDirectoryIdentity,
        string canonicalDestinationDirectoryPath,
        FileIdentity destinationDirectoryIdentity,
        string canonicalSourcePath,
        FileIdentity sourceIdentity,
        string canonicalDestinationPath,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destinationContentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDestinationDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDestinationPath);
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        if (entry.IsDirectory)
        {
            throw new ArgumentException(
                "Cross-volume Move source deletion supports regular files only.",
                nameof(entry));
        }
        if (sourceDirectoryIdentity.VolumeSerialNumber == destinationDirectoryIdentity.VolumeSerialNumber ||
            sourceIdentity.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber ||
            destinationIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Cross-volume Move source-delete evidence must remain bound to distinct source/destination root volumes.");
        }

        OperationId = operationId;
        Ordinal = ordinal;
        Entry = entry;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        CanonicalDestinationDirectoryPath = canonicalDestinationDirectoryPath;
        DestinationDirectoryIdentity = destinationDirectoryIdentity;
        CanonicalSourcePath = canonicalSourcePath;
        SourceIdentity = sourceIdentity;
        CanonicalDestinationPath = canonicalDestinationPath;
        DestinationIdentity = destinationIdentity;
        DestinationContentFingerprint = destinationContentFingerprint;
    }

    public Guid OperationId { get; }

    public int Ordinal { get; }

    public FileOperationEntry Entry { get; }

    public string CanonicalSourceDirectoryPath { get; }

    public FileIdentity SourceDirectoryIdentity { get; }

    public string CanonicalDestinationDirectoryPath { get; }

    public FileIdentity DestinationDirectoryIdentity { get; }

    public string CanonicalSourcePath { get; }

    public FileIdentity SourceIdentity { get; }

    public string CanonicalDestinationPath { get; }

    public FileIdentity DestinationIdentity { get; }

    public FileContentFingerprint DestinationContentFingerprint { get; }

    public bool SourceDeleteMutationAuthorized => false;
}

/// <summary>
/// Provider-owned value evidence for one still-live source-delete capability lease.
/// It carries no reusable operating-system handle and grants no mutation authority by itself.
/// Construction is public because Windows providers live in a separate assembly; possession
/// of this value still cannot mint mutation authority without Core's exact durable barrier.
/// </summary>
public sealed class FileCrossVolumeMoveSourceDeleteEvidence
{
    public FileCrossVolumeMoveSourceDeleteEvidence(
        FileCrossVolumeMoveSourceDeleteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
    }

    public FileCrossVolumeMoveSourceDeleteRequest Request { get; }

    public Guid OperationId => Request.OperationId;

    public int Ordinal => Request.Ordinal;

    public FileOperationEntry Entry => Request.Entry;

    public string CanonicalSourceDirectoryPath => Request.CanonicalSourceDirectoryPath;

    public FileIdentity SourceDirectoryIdentity => Request.SourceDirectoryIdentity;

    public string CanonicalDestinationDirectoryPath => Request.CanonicalDestinationDirectoryPath;

    public FileIdentity DestinationDirectoryIdentity => Request.DestinationDirectoryIdentity;

    public string CanonicalSourcePath => Request.CanonicalSourcePath;

    public FileIdentity SourceIdentity => Request.SourceIdentity;

    public string CanonicalDestinationPath => Request.CanonicalDestinationPath;

    public FileIdentity DestinationIdentity => Request.DestinationIdentity;

    public FileContentFingerprint DestinationContentFingerprint =>
        Request.DestinationContentFingerprint;

    public bool SourceDeleteMutationAuthorized => false;
}

/// <summary>
/// Core-minted one-shot authority created only after the composite journal has durably
/// transitioned this exact entry to SourceDeleteStarted while the exact provider lease is live.
/// </summary>
public sealed class FileCrossVolumeMoveSourceDeleteAuthorization
{
    internal FileCrossVolumeMoveSourceDeleteAuthorization(
        FileCrossVolumeMoveSourceDeleteEvidence evidence,
        FileCrossVolumeMoveActionHistory barrierHistory)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(barrierHistory);
        if (barrierHistory.OperationId != evidence.OperationId ||
            evidence.Ordinal < 0 ||
            evidence.Ordinal >= barrierHistory.Entries.Count)
        {
            throw new InvalidOperationException(
                "Cross-volume Move source-delete authorization requires the exact composite history operation/entry.");
        }

        var entry = barrierHistory.Entries[evidence.Ordinal];
        if (entry.State != FileCrossVolumeMoveEntryState.SourceDeleteStarted ||
            entry.Entry != evidence.Entry ||
            entry.SourceIdentity != evidence.SourceIdentity ||
            entry.DestinationIdentity != evidence.DestinationIdentity ||
            entry.DestinationContentFingerprint != evidence.DestinationContentFingerprint ||
            !PathEquals(entry.CanonicalSourcePath, evidence.CanonicalSourcePath) ||
            !PathEquals(entry.CanonicalDestinationPath, evidence.CanonicalDestinationPath) ||
            barrierHistory.SourceDirectoryIdentity != evidence.SourceDirectoryIdentity ||
            barrierHistory.DestinationDirectoryIdentity != evidence.DestinationDirectoryIdentity ||
            !PathEquals(
                barrierHistory.CanonicalSourceDirectoryPath,
                evidence.CanonicalSourceDirectoryPath) ||
            !PathEquals(
                barrierHistory.CanonicalDestinationDirectoryPath,
                evidence.CanonicalDestinationDirectoryPath))
        {
            throw new InvalidOperationException(
                "Cross-volume Move source-delete barrier history does not match the live pre-barrier capability evidence.");
        }

        Evidence = evidence;
        BarrierHistory = barrierHistory;
    }

    public FileCrossVolumeMoveSourceDeleteEvidence Evidence { get; }

    public FileCrossVolumeMoveActionHistory BarrierHistory { get; }

    public Guid OperationId => Evidence.OperationId;

    public int Ordinal => Evidence.Ordinal;

    public FileIdentity SourceIdentity => Evidence.SourceIdentity;

    public bool SourceDeleteBarrierSatisfied => true;

    public bool SourceDeleteMutationAuthorized => true;

    /// <summary>
    /// Allows a separate Windows provider assembly to prove that one post-barrier authority
    /// refers to this exact provider evidence object. The check grants no new authority.
    /// </summary>
    public bool IsBoundTo(FileCrossVolumeMoveSourceDeleteEvidence evidence) =>
        ReferenceEquals(Evidence, evidence) &&
        OperationId == evidence.OperationId &&
        Ordinal == evidence.Ordinal &&
        SourceIdentity == evidence.SourceIdentity;

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public interface IFileCrossVolumeMoveSourceDeleteLease : IAsyncDisposable
{
    FileCrossVolumeMoveSourceDeleteEvidence Evidence { get; }

    bool DeleteAccessCapabilityHeld { get; }

    bool SourceDeleteMutationPerformed { get; }

    /// <summary>
    /// Positive provider receipt that the exact source handle which received the one-shot
    /// POSIX delete disposition completed its checked close boundary. Cleanup/disposal is
    /// deliberately not equivalent to this receipt.
    /// </summary>
    bool SourceDeleteHandleCloseCompleted { get; }

    ValueTask MarkDeletePendingAsync(
        FileCrossVolumeMoveSourceDeleteAuthorization authorization,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the exact source-delete handle close once, after disposition has been
    /// performed under the same Core-minted post-barrier authority. Failures after the
    /// durable SourceDeleteStarted barrier are recovery-sensitive.
    /// </summary>
    ValueTask CloseSourceDeleteHandleAsync(
        FileCrossVolumeMoveSourceDeleteAuthorization authorization,
        CancellationToken cancellationToken = default);
}

public interface IFileCrossVolumeMoveSourceDeletePrimitive
{
    /// <summary>
    /// Acquires and validates the exact source/root DELETE-capable handle set before the
    /// durable source-delete barrier. Predictable capability/platform refusal therefore
    /// occurs while the source is still retained and the journal remains DestinationCommitted.
    /// </summary>
    ValueTask<IFileCrossVolumeMoveSourceDeleteLease> AcquireAsync(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken = default);
}
