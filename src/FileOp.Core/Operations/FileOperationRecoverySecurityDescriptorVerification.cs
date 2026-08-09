using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public sealed record FileSecurityDescriptorReadResult(
    FileContentFingerprintReadStatus Status,
    FileOperationCanonicalPath CurrentDestinationDirectory,
    FileOperationCanonicalPath CurrentDestination,
    FileSecurityDescriptorEvidence? SecurityDescriptor,
    string Message);

public interface IRootBoundFileSecurityDescriptorEvidenceReader
{
    ValueTask<FileSecurityDescriptorReadResult> ReadAsync(
        FileContentFingerprintReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record FileOperationRecoverySecurityDescriptorVerificationItem(
    int Ordinal,
    FileOperationRecoveryInspectionItem Inspection,
    FileContentFingerprintReadStatus? ReaderStatus,
    FileOperationRecoverySecurityDescriptorComparison Comparison,
    string Message)
{
    public bool SameQueriedDescriptorBytes => Comparison.SameQueriedDescriptorBytes;
}

public sealed record FileOperationRecoverySecurityDescriptorVerification
{
    public FileOperationRecoverySecurityDescriptorVerification(
        Guid operationId,
        IEnumerable<FileOperationRecoverySecurityDescriptorVerificationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        OperationId = operationId;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public Guid OperationId { get; }

    public IReadOnlyList<FileOperationRecoverySecurityDescriptorVerificationItem> Items { get; }

    public int SameQueriedDescriptorBytesCount =>
        Items.Count(static item => item.SameQueriedDescriptorBytes);
}

/// <summary>
/// Performs a separate root-bound owner/group/DACL observation and compares only
/// its exact queried self-relative descriptor bytes with durable SHA-256 evidence.
/// This verifier grants no mutation authority.
/// </summary>
public sealed class FileOperationRecoverySecurityDescriptorVerifier
{
    private readonly IFileOperationActionHistorySecurityDescriptorEvidenceStore _evidenceStore;
    private readonly IRootBoundFileSecurityDescriptorEvidenceReader _reader;

    public FileOperationRecoverySecurityDescriptorVerifier(
        IFileOperationActionHistorySecurityDescriptorEvidenceStore evidenceStore,
        IRootBoundFileSecurityDescriptorEvidenceReader reader)
    {
        _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async ValueTask<FileOperationRecoverySecurityDescriptorVerification> VerifyAsync(
        FileOperationRecoveryInspection inspection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var durableHistory = await _evidenceStore
            .GetAsync(inspection.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<FileOperationRecoverySecurityDescriptorVerificationItem>(inspection.Items.Count);

        foreach (var item in inspection.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesDurableHistory(durableHistory, inspection, item))
            {
                results.Add(Create(
                    item,
                    ReaderStatus: null,
                    UnavailableWithoutRecordedEvidence(),
                    "Recovery inspection provenance no longer matches durable action history; security evidence is not trusted."));
                continue;
            }

            var recorded = await _evidenceStore
                .GetDestinationSecurityDescriptorEvidenceAsync(
                    inspection.OperationId,
                    item.Ordinal,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recorded is null)
            {
                results.Add(Create(
                    item,
                    ReaderStatus: null,
                    FileOperationRecoverySecurityDescriptorComparer.Compare(null, null),
                    "Durable history does not contain owner/group/DACL security-descriptor evidence."));
                continue;
            }

            if (!TryCreateRequest(inspection, item, out var request))
            {
                results.Add(Create(
                    item,
                    ReaderStatus: null,
                    FileOperationRecoverySecurityDescriptorComparer.Compare(recorded, current: null),
                    "Current root/leaf provenance is not strong enough for security-descriptor comparison."));
                continue;
            }

            FileSecurityDescriptorReadResult read;
            try
            {
                read = await _reader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.Add(Create(
                    item,
                    FileContentFingerprintReadStatus.Error,
                    FileOperationRecoverySecurityDescriptorComparer.Compare(recorded, current: null),
                    "Root-bound security-descriptor verification failed: " + exception.Message));
                continue;
            }

            if (read.Status != FileContentFingerprintReadStatus.Success ||
                !IsConsistentSuccess(request, read))
            {
                results.Add(Create(
                    item,
                    read.Status,
                    FileOperationRecoverySecurityDescriptorComparer.Compare(recorded, current: null),
                    string.IsNullOrWhiteSpace(read.Message)
                        ? "Current owner/group/DACL descriptor could not be verified reliably."
                        : read.Message));
                continue;
            }

            results.Add(Create(
                item,
                read.Status,
                FileOperationRecoverySecurityDescriptorComparer.Compare(recorded, read.SecurityDescriptor),
                "Current root-bound owner/group/DACL descriptor bytes were compared with the durable post-Copy digest."));
        }

        return new FileOperationRecoverySecurityDescriptorVerification(inspection.OperationId, results);
    }

    private static bool MatchesDurableHistory(
        FileOperationActionHistory? history,
        FileOperationRecoveryInspection inspection,
        FileOperationRecoveryInspectionItem item)
    {
        var recordedRootPath = inspection.DestinationDirectory.RecordedCanonicalPath;
        if (history is null ||
            history.OperationId != inspection.OperationId ||
            history.Kind != FileOperationKind.Copy ||
            history.DestinationDirectoryIdentity is not FileIdentity durableRootIdentity ||
            inspection.DestinationDirectory.RecordedIdentity != durableRootIdentity ||
            string.IsNullOrWhiteSpace(recordedRootPath) ||
            !PathsEqual(history.CanonicalDestinationDirectoryPath, recordedRootPath) ||
            item.Ordinal < 0 ||
            item.Ordinal >= history.Entries.Count)
        {
            return false;
        }

        var durable = history.Entries[item.Ordinal];
        var observed = item.Entry;
        return durable.Ordinal == item.Ordinal &&
            durable.State == observed.State &&
            durable.SourceIdentity == observed.SourceIdentity &&
            durable.DestinationIdentity == observed.DestinationIdentity &&
            durable.DestinationContentFingerprint == observed.DestinationContentFingerprint &&
            durable.DestinationHardLinkCount == observed.DestinationHardLinkCount &&
            durable.Entry.IsDirectory == observed.Entry.IsDirectory &&
            string.Equals(durable.Entry.Name, observed.Entry.Name, StringComparison.Ordinal) &&
            PathsEqual(durable.Entry.Path, observed.Entry.Path) &&
            PathsEqual(durable.CanonicalSourcePath, observed.CanonicalSourcePath) &&
            PathsEqual(durable.CanonicalDestinationPath, observed.CanonicalDestinationPath);
    }

    private static FileOperationRecoverySecurityDescriptorComparison UnavailableWithoutRecordedEvidence() =>
        new(
            FileOperationRecoverySecurityDescriptorStatus.Unavailable,
            Recorded: null,
            Current: null,
            SecurityInformationMaskMatches: null,
            Sha256Matches: null);

    private static bool TryCreateRequest(
        FileOperationRecoveryInspection inspection,
        FileOperationRecoveryInspectionItem item,
        out FileContentFingerprintReadRequest request)
    {
        request = null!;
        var recordedRootPath = inspection.DestinationDirectory.RecordedCanonicalPath;
        if (!inspection.DestinationDirectory.IsSameRecordedRoot ||
            inspection.DestinationDirectory.RecordedIdentity is not FileIdentity rootIdentity ||
            string.IsNullOrWhiteSpace(recordedRootPath) ||
            inspection.DestinationDirectory.CurrentDirectory is not { } currentRoot ||
            currentRoot.State != FileOperationCanonicalPathState.Directory ||
            currentRoot.IsLeafReparsePoint ||
            currentRoot.Identity != rootIdentity ||
            !PathsEqual(recordedRootPath, currentRoot.CanonicalPath) ||
            item.Status != FileOperationRecoveryDestinationStatus.SameObject ||
            item.RecordedDestinationIdentity is not FileIdentity destinationIdentity)
        {
            return false;
        }

        request = new FileContentFingerprintReadRequest(
            recordedRootPath,
            rootIdentity,
            item.Entry.CanonicalDestinationPath,
            destinationIdentity);
        return true;
    }

    private static bool IsConsistentSuccess(
        FileContentFingerprintReadRequest request,
        FileSecurityDescriptorReadResult read) =>
        read.SecurityDescriptor is not null &&
        read.SecurityDescriptor.SecurityInformation == FileSecurityDescriptorEvidence.QueriedSecurityInformationMask &&
        read.CurrentDestinationDirectory.State == FileOperationCanonicalPathState.Directory &&
        !read.CurrentDestinationDirectory.IsLeafReparsePoint &&
        read.CurrentDestinationDirectory.Identity == request.DestinationDirectoryIdentity &&
        PathsEqual(request.CanonicalDestinationDirectoryPath, read.CurrentDestinationDirectory.CanonicalPath) &&
        read.CurrentDestination.State == FileOperationCanonicalPathState.File &&
        !read.CurrentDestination.IsLeafReparsePoint &&
        read.CurrentDestination.Identity == request.DestinationIdentity &&
        PathsEqual(request.CanonicalDestinationPath, read.CurrentDestination.CanonicalPath);

    private static FileOperationRecoverySecurityDescriptorVerificationItem Create(
        FileOperationRecoveryInspectionItem item,
        FileContentFingerprintReadStatus? ReaderStatus,
        FileOperationRecoverySecurityDescriptorComparison comparison,
        string message) =>
        new(item.Ordinal, item, ReaderStatus, comparison, message);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd('\\', '/'),
            right.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}
