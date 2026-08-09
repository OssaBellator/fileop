using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public sealed record FileBasicMetadataReadResult(
    FileContentFingerprintReadStatus Status,
    FileOperationCanonicalPath CurrentDestinationDirectory,
    FileOperationCanonicalPath CurrentDestination,
    FileBasicMetadataEvidence? BasicMetadata,
    string Message);

public interface IRootBoundFileBasicMetadataEvidenceReader
{
    ValueTask<FileBasicMetadataReadResult> ReadAsync(
        FileContentFingerprintReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record FileOperationRecoveryBasicMetadataVerificationItem(
    int Ordinal,
    FileOperationRecoveryInspectionItem Inspection,
    FileContentFingerprintReadStatus? ReaderStatus,
    FileOperationRecoveryBasicMetadataComparison Comparison,
    string Message)
{
    public bool SameStableMetadata => Comparison.SameStableMetadata;
}

public sealed record FileOperationRecoveryBasicMetadataVerification
{
    public FileOperationRecoveryBasicMetadataVerification(
        Guid operationId,
        IEnumerable<FileOperationRecoveryBasicMetadataVerificationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        OperationId = operationId;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public Guid OperationId { get; }

    public IReadOnlyList<FileOperationRecoveryBasicMetadataVerificationItem> Items { get; }

    public int SameStableMetadataCount => Items.Count(static item => item.SameStableMetadata);
}

/// <summary>
/// Performs a separate root-bound current basic-metadata observation and compares
/// it with durable commit-time evidence. This stage grants no mutation authority.
/// </summary>
public sealed class FileOperationRecoveryBasicMetadataVerifier
{
    private readonly IFileOperationActionHistoryBasicMetadataEvidenceStore _evidenceStore;
    private readonly IRootBoundFileBasicMetadataEvidenceReader _reader;

    public FileOperationRecoveryBasicMetadataVerifier(
        IFileOperationActionHistoryBasicMetadataEvidenceStore evidenceStore,
        IRootBoundFileBasicMetadataEvidenceReader reader)
    {
        _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async ValueTask<FileOperationRecoveryBasicMetadataVerification> VerifyAsync(
        FileOperationRecoveryInspection inspection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var results = new List<FileOperationRecoveryBasicMetadataVerificationItem>(inspection.Items.Count);

        foreach (var item in inspection.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recorded = await _evidenceStore
                .GetDestinationBasicMetadataEvidenceAsync(
                    inspection.OperationId,
                    item.Ordinal,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recorded is null)
            {
                results.Add(Create(
                    item,
                    ReaderStatus: null,
                    FileOperationRecoveryBasicMetadataComparer.Compare(null, null),
                    "Durable history does not contain post-Copy basic metadata evidence."));
                continue;
            }

            if (!TryCreateRequest(inspection, item, out var request))
            {
                results.Add(Create(
                    item,
                    ReaderStatus: null,
                    FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current: null),
                    "Current root/leaf provenance is not strong enough for basic-metadata comparison."));
                continue;
            }

            FileBasicMetadataReadResult read;
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
                    FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current: null),
                    "Root-bound basic-metadata verification failed: " + exception.Message));
                continue;
            }

            if (read.Status != FileContentFingerprintReadStatus.Success ||
                !IsConsistentSuccess(request, read))
            {
                results.Add(Create(
                    item,
                    read.Status,
                    FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current: null),
                    string.IsNullOrWhiteSpace(read.Message)
                        ? "Current basic metadata could not be verified reliably."
                        : read.Message));
                continue;
            }

            results.Add(Create(
                item,
                read.Status,
                FileOperationRecoveryBasicMetadataComparer.Compare(recorded, read.BasicMetadata),
                "Current root-bound basic metadata was compared with the durable post-Copy snapshot. Last-access equality is diagnostic only."));
        }

        return new FileOperationRecoveryBasicMetadataVerification(inspection.OperationId, results);
    }

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
        FileBasicMetadataReadResult read) =>
        read.BasicMetadata is not null &&
        read.CurrentDestinationDirectory.State == FileOperationCanonicalPathState.Directory &&
        !read.CurrentDestinationDirectory.IsLeafReparsePoint &&
        read.CurrentDestinationDirectory.Identity == request.DestinationDirectoryIdentity &&
        PathsEqual(
            request.CanonicalDestinationDirectoryPath,
            read.CurrentDestinationDirectory.CanonicalPath) &&
        read.CurrentDestination.State == FileOperationCanonicalPathState.File &&
        !read.CurrentDestination.IsLeafReparsePoint &&
        read.CurrentDestination.Identity == request.DestinationIdentity &&
        PathsEqual(request.CanonicalDestinationPath, read.CurrentDestination.CanonicalPath);

    private static FileOperationRecoveryBasicMetadataVerificationItem Create(
        FileOperationRecoveryInspectionItem item,
        FileContentFingerprintReadStatus? ReaderStatus,
        FileOperationRecoveryBasicMetadataComparison comparison,
        string message) =>
        new(item.Ordinal, item, ReaderStatus, comparison, message);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd('\\', '/'),
            right.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}
