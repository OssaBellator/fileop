using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public sealed record FileNamedDataStreamTopologyReadResult(
    FileContentFingerprintReadStatus Status,
    FileOperationCanonicalPath CurrentDestinationDirectory,
    FileOperationCanonicalPath CurrentDestination,
    FileNamedDataStreamTopologyEvidence? Topology,
    string Message);

public interface IRootBoundFileNamedDataStreamTopologyEvidenceReader
{
    ValueTask<FileNamedDataStreamTopologyReadResult> ReadAsync(
        FileContentFingerprintReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record FileOperationRecoveryNamedDataStreamTopologyVerificationItem(
    int Ordinal,
    FileOperationRecoveryInspectionItem Inspection,
    FileContentFingerprintReadStatus? ReaderStatus,
    FileOperationRecoveryNamedDataStreamTopologyComparison Comparison,
    string Message)
{
    public bool SameNamesAndSizes => Comparison.SameNamesAndSizes;
}

public sealed record FileOperationRecoveryNamedDataStreamTopologyVerification
{
    public FileOperationRecoveryNamedDataStreamTopologyVerification(
        Guid operationId,
        IEnumerable<FileOperationRecoveryNamedDataStreamTopologyVerificationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        OperationId = operationId;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public Guid OperationId { get; }

    public IReadOnlyList<FileOperationRecoveryNamedDataStreamTopologyVerificationItem> Items { get; }

    public int SameNamesAndSizesCount => Items.Count(static item => item.SameNamesAndSizes);
}

/// <summary>
/// Compares current root-bound named-$DATA-stream names/sizes with durable digest
/// evidence. This stage does not hash named-stream contents and grants no mutation authority.
/// </summary>
public sealed class FileOperationRecoveryNamedDataStreamTopologyVerifier
{
    private readonly IFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore _evidenceStore;
    private readonly IRootBoundFileNamedDataStreamTopologyEvidenceReader _reader;

    public FileOperationRecoveryNamedDataStreamTopologyVerifier(
        IFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore evidenceStore,
        IRootBoundFileNamedDataStreamTopologyEvidenceReader reader)
    {
        _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async ValueTask<FileOperationRecoveryNamedDataStreamTopologyVerification> VerifyAsync(
        FileOperationRecoveryInspection inspection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var durableHistory = await _evidenceStore
            .GetAsync(inspection.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<FileOperationRecoveryNamedDataStreamTopologyVerificationItem>(inspection.Items.Count);

        foreach (var item in inspection.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesDurableHistory(durableHistory, inspection, item))
            {
                results.Add(Create(
                    item,
                    ReaderStatus: null,
                    UnavailableWithoutRecordedEvidence(),
                    "Recovery inspection provenance no longer matches durable action history; named-stream evidence is not trusted."));
                continue;
            }

            var recorded = await _evidenceStore
                .GetDestinationNamedDataStreamTopologyEvidenceAsync(
                    inspection.OperationId,
                    item.Ordinal,
                    cancellationToken)
                .ConfigureAwait(false);
            if (recorded is null)
            {
                results.Add(Create(
                    item,
                    ReaderStatus: null,
                    FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(null, null),
                    "Durable history does not contain named-data-stream topology evidence."));
                continue;
            }

            if (!TryCreateRequest(inspection, item, out var request))
            {
                results.Add(Create(
                    item,
                    ReaderStatus: null,
                    FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(recorded, current: null),
                    "Current root/leaf provenance is not strong enough for named-stream topology comparison."));
                continue;
            }

            FileNamedDataStreamTopologyReadResult read;
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
                    FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(recorded, current: null),
                    "Root-bound named-stream topology verification failed: " + exception.Message));
                continue;
            }

            if (read.Status != FileContentFingerprintReadStatus.Success ||
                !IsConsistentSuccess(request, read))
            {
                results.Add(Create(
                    item,
                    read.Status,
                    FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(recorded, current: null),
                    string.IsNullOrWhiteSpace(read.Message)
                        ? "Current named-data-stream topology could not be verified reliably."
                        : read.Message));
                continue;
            }

            results.Add(Create(
                item,
                read.Status,
                FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(recorded, read.Topology),
                "Current root-bound named-$DATA-stream names and logical sizes were compared with durable post-Copy topology evidence."));
        }

        return new FileOperationRecoveryNamedDataStreamTopologyVerification(inspection.OperationId, results);
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

    private static FileOperationRecoveryNamedDataStreamTopologyComparison UnavailableWithoutRecordedEvidence() =>
        new(
            FileOperationRecoveryNamedDataStreamTopologyStatus.Unavailable,
            Recorded: null,
            Current: null,
            FormatVersionMatches: null,
            NamedStreamCountMatches: null,
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
        FileNamedDataStreamTopologyReadResult read) =>
        read.Topology is not null &&
        read.CurrentDestinationDirectory.State == FileOperationCanonicalPathState.Directory &&
        !read.CurrentDestinationDirectory.IsLeafReparsePoint &&
        read.CurrentDestinationDirectory.Identity == request.DestinationDirectoryIdentity &&
        PathsEqual(request.CanonicalDestinationDirectoryPath, read.CurrentDestinationDirectory.CanonicalPath) &&
        read.CurrentDestination.State == FileOperationCanonicalPathState.File &&
        !read.CurrentDestination.IsLeafReparsePoint &&
        read.CurrentDestination.Identity == request.DestinationIdentity &&
        PathsEqual(request.CanonicalDestinationPath, read.CurrentDestination.CanonicalPath);

    private static FileOperationRecoveryNamedDataStreamTopologyVerificationItem Create(
        FileOperationRecoveryInspectionItem item,
        FileContentFingerprintReadStatus? ReaderStatus,
        FileOperationRecoveryNamedDataStreamTopologyComparison comparison,
        string message) =>
        new(item.Ordinal, item, ReaderStatus, comparison, message);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd('\\', '/'),
            right.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}
