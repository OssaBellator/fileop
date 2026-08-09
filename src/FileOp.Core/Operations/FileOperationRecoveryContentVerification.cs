using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum FileContentFingerprintReadStatus
{
    Success,
    Missing,
    DifferentObject,
    Redirected,
    ReparsePoint,
    UnexpectedType,
    Busy,
    Inaccessible,
    Error,
    DestinationRootChanged,
}

public sealed record FileContentFingerprintReadResult(
    FileContentFingerprintReadStatus Status,
    FileOperationCanonicalPath CurrentDestination,
    FileContentFingerprint? ContentFingerprint,
    string Message,
    FileOperationCanonicalPath? CurrentDestinationDirectory = null)
{
    public FileContentFingerprintReadResult(
        FileContentFingerprintReadStatus status,
        FileOperationCanonicalPath currentDestination,
        FileContentFingerprint? contentFingerprint,
        string message)
        : this(status, currentDestination, contentFingerprint, message, CurrentDestinationDirectory: null)
    {
    }

    public void Deconstruct(
        out FileContentFingerprintReadStatus status,
        out FileOperationCanonicalPath currentDestination,
        out FileContentFingerprint? contentFingerprint,
        out string message)
    {
        status = Status;
        currentDestination = CurrentDestination;
        contentFingerprint = ContentFingerprint;
        message = Message;
    }
}

/// <summary>
/// Legacy leaf-only reader contract retained for compatibility. Recovery content
/// verification no longer depends on this contract because it cannot hold the
/// destination-root binding.
/// </summary>
public interface IFileContentFingerprintReader
{
    ValueTask<FileContentFingerprintReadResult> ReadAsync(
        string canonicalPath,
        FileIdentity expectedIdentity,
        CancellationToken cancellationToken = default);
}

public sealed record FileContentFingerprintReadRequest(
    string CanonicalDestinationDirectoryPath,
    FileIdentity DestinationDirectoryIdentity,
    string CanonicalDestinationPath,
    FileIdentity DestinationIdentity);

/// <summary>
/// Reads a destination leaf relative to the recorded destination-root binding
/// and keeps both root and leaf handles alive while producing SHA-256 evidence.
/// </summary>
public interface IRootBoundFileContentFingerprintReader
{
    ValueTask<FileContentFingerprintReadResult> ReadAsync(
        FileContentFingerprintReadRequest request,
        CancellationToken cancellationToken = default);
}

public enum FileOperationRecoveryContentStatus
{
    NoRecordedFingerprint,
    DestinationRootNotVerified,
    NotSameRecordedObject,
    MatchesRecordedMainStream,
    DifferentMainStream,
    Missing,
    DifferentObject,
    Redirected,
    ReparsePoint,
    UnexpectedType,
    Busy,
    Inaccessible,
    Error,
    DestinationRootChanged,
}

public sealed record FileOperationRecoveryContentVerificationItem(
    int Ordinal,
    FileOperationRecoveryInspectionItem Inspection,
    FileOperationRecoveryContentStatus Status,
    FileContentFingerprint? CurrentContentFingerprint,
    string Message)
{
    public FileContentFingerprint? RecordedContentFingerprint =>
        Inspection.Entry.DestinationContentFingerprint;

    public bool MatchesRecordedMainStream =>
        Status == FileOperationRecoveryContentStatus.MatchesRecordedMainStream;
}

public sealed record FileOperationRecoveryContentVerification
{
    public FileOperationRecoveryContentVerification(
        Guid operationId,
        IEnumerable<FileOperationRecoveryContentVerificationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        OperationId = operationId;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public Guid OperationId { get; }

    public IReadOnlyList<FileOperationRecoveryContentVerificationItem> Items { get; }

    public int MatchingMainStreamCount =>
        Items.Count(static item => item.MatchesRecordedMainStream);
}

public interface IFileOperationRecoveryContentVerifier
{
    ValueTask<FileOperationRecoveryContentVerification> VerifyAsync(
        FileOperationRecoveryInspection inspection,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Compares durable post-Copy SHA-256 evidence with the current primary data
/// stream only after read-only recovery inspection has established both the
/// recorded destination root and destination file as the same stable objects.
/// The root-bound reader must then re-prove and hold both namespace bindings.
/// A match is evidence only and grants no mutation, delete, recovery, or Undo authority.
/// </summary>
public sealed class FileOperationRecoveryContentVerifier : IFileOperationRecoveryContentVerifier
{
    private readonly IRootBoundFileContentFingerprintReader _reader;

    public FileOperationRecoveryContentVerifier(IRootBoundFileContentFingerprintReader reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public async ValueTask<FileOperationRecoveryContentVerification> VerifyAsync(
        FileOperationRecoveryInspection inspection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var results = new List<FileOperationRecoveryContentVerificationItem>(inspection.Items.Count);

        foreach (var item in inspection.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = item.Entry;
            if (entry.DestinationContentFingerprint is not FileContentFingerprint recorded)
            {
                results.Add(Create(
                    item,
                    FileOperationRecoveryContentStatus.NoRecordedFingerprint,
                    currentFingerprint: null,
                    "Durable recovery history does not contain a post-Copy content fingerprint."));
                continue;
            }

            var recordedDirectoryPath = inspection.DestinationDirectory.RecordedCanonicalPath;
            if (!inspection.DestinationDirectory.IsSameRecordedRoot ||
                inspection.DestinationDirectory.RecordedIdentity is not FileIdentity expectedDirectoryIdentity ||
                string.IsNullOrWhiteSpace(recordedDirectoryPath) ||
                inspection.DestinationDirectory.CurrentDirectory is not { } currentDirectory ||
                currentDirectory.State != FileOperationCanonicalPathState.Directory ||
                currentDirectory.IsLeafReparsePoint ||
                currentDirectory.Identity != expectedDirectoryIdentity ||
                !PathsEqual(recordedDirectoryPath, currentDirectory.CanonicalPath))
            {
                results.Add(Create(
                    item,
                    FileOperationRecoveryContentStatus.DestinationRootNotVerified,
                    currentFingerprint: null,
                    $"Main-stream verification is skipped because the destination root is not verified as the recorded stable directory ({inspection.DestinationDirectory.Status})."));
                continue;
            }

            if (item.Status != FileOperationRecoveryDestinationStatus.SameObject ||
                item.RecordedDestinationIdentity is not FileIdentity expectedIdentity)
            {
                results.Add(Create(
                    item,
                    FileOperationRecoveryContentStatus.NotSameRecordedObject,
                    currentFingerprint: null,
                    "Main-stream verification is skipped unless recovery inspection proves the recorded destination canonical location and FileIdentity."));
                continue;
            }

            var request = new FileContentFingerprintReadRequest(
                recordedDirectoryPath,
                expectedDirectoryIdentity,
                entry.CanonicalDestinationPath,
                expectedIdentity);

            FileContentFingerprintReadResult read;
            try
            {
                read = await _reader
                    .ReadAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.Add(Create(
                    item,
                    FileOperationRecoveryContentStatus.Error,
                    currentFingerprint: null,
                    "Stable root-bound content verification failed: " + exception.Message));
                continue;
            }

            results.Add(Classify(item, request, recorded, read));
        }

        return new FileOperationRecoveryContentVerification(inspection.OperationId, results);
    }

    private static FileOperationRecoveryContentVerificationItem Classify(
        FileOperationRecoveryInspectionItem inspection,
        FileContentFingerprintReadRequest request,
        FileContentFingerprint recorded,
        FileContentFingerprintReadResult read)
    {
        if (read.Status == FileContentFingerprintReadStatus.Success &&
            !IsConsistentSuccess(request, read))
        {
            return Create(
                inspection,
                FileOperationRecoveryContentStatus.Error,
                currentFingerprint: null,
                "The content reader reported success without consistent root/leaf canonical path, type, identity, or SHA-256 evidence.");
        }

        var status = read.Status switch
        {
            FileContentFingerprintReadStatus.Success when read.ContentFingerprint == recorded =>
                FileOperationRecoveryContentStatus.MatchesRecordedMainStream,
            FileContentFingerprintReadStatus.Success =>
                FileOperationRecoveryContentStatus.DifferentMainStream,
            FileContentFingerprintReadStatus.DestinationRootChanged =>
                FileOperationRecoveryContentStatus.DestinationRootChanged,
            FileContentFingerprintReadStatus.Missing => FileOperationRecoveryContentStatus.Missing,
            FileContentFingerprintReadStatus.DifferentObject => FileOperationRecoveryContentStatus.DifferentObject,
            FileContentFingerprintReadStatus.Redirected => FileOperationRecoveryContentStatus.Redirected,
            FileContentFingerprintReadStatus.ReparsePoint => FileOperationRecoveryContentStatus.ReparsePoint,
            FileContentFingerprintReadStatus.UnexpectedType => FileOperationRecoveryContentStatus.UnexpectedType,
            FileContentFingerprintReadStatus.Busy => FileOperationRecoveryContentStatus.Busy,
            FileContentFingerprintReadStatus.Inaccessible => FileOperationRecoveryContentStatus.Inaccessible,
            FileContentFingerprintReadStatus.Error => FileOperationRecoveryContentStatus.Error,
            _ => FileOperationRecoveryContentStatus.Error,
        };

        return Create(
            inspection,
            status,
            status is FileOperationRecoveryContentStatus.MatchesRecordedMainStream or
                FileOperationRecoveryContentStatus.DifferentMainStream
                ? read.ContentFingerprint
                : null,
            Describe(status, read.Message));
    }

    private static bool IsConsistentSuccess(
        FileContentFingerprintReadRequest request,
        FileContentFingerprintReadResult read) =>
        read.ContentFingerprint is
            { Algorithm: FileContentFingerprintAlgorithm.Sha256 } &&
        read.CurrentDestinationDirectory is { } root &&
        root.State == FileOperationCanonicalPathState.Directory &&
        !root.IsLeafReparsePoint &&
        root.Identity is FileIdentity actualDirectoryIdentity &&
        actualDirectoryIdentity == request.DestinationDirectoryIdentity &&
        PathsEqual(request.CanonicalDestinationDirectoryPath, root.CanonicalPath) &&
        read.CurrentDestination.State == FileOperationCanonicalPathState.File &&
        !read.CurrentDestination.IsLeafReparsePoint &&
        read.CurrentDestination.Identity is FileIdentity actualIdentity &&
        actualIdentity == request.DestinationIdentity &&
        PathsEqual(request.CanonicalDestinationPath, read.CurrentDestination.CanonicalPath);

    private static FileOperationRecoveryContentVerificationItem Create(
        FileOperationRecoveryInspectionItem inspection,
        FileOperationRecoveryContentStatus status,
        FileContentFingerprint? currentFingerprint,
        string message) =>
        new(
            inspection.Ordinal,
            inspection,
            status,
            currentFingerprint,
            message);

    private static string Describe(
        FileOperationRecoveryContentStatus status,
        string readerMessage) => status switch
    {
        FileOperationRecoveryContentStatus.MatchesRecordedMainStream =>
            "The root-bound identity-verified destination handle produced the same primary-stream SHA-256 as the post-Copy record. This is evidence only, not mutation authorization.",
        FileOperationRecoveryContentStatus.DifferentMainStream =>
            "The same recorded destination object under the verified destination root now has a different primary-stream SHA-256 than the post-Copy record.",
        FileOperationRecoveryContentStatus.DestinationRootChanged =>
            "The destination-root binding changed before or during stable content verification, so the main stream is not trusted as recovery evidence.",
        FileOperationRecoveryContentStatus.Missing =>
            "The destination became missing before a stable content-read handle could verify it.",
        FileOperationRecoveryContentStatus.DifferentObject =>
            "The destination changed to a different FileIdentity before stable content verification.",
        FileOperationRecoveryContentStatus.Redirected =>
            "The destination resolved to a different canonical location before stable content verification.",
        FileOperationRecoveryContentStatus.ReparsePoint =>
            "The destination is now a reparse point and its content is not trusted for recovery evidence.",
        FileOperationRecoveryContentStatus.UnexpectedType =>
            "The recorded file destination now resolves to a non-file object.",
        FileOperationRecoveryContentStatus.Busy =>
            "The destination root or leaf could not be opened with the required sharing constraints; a stable root-bound content proof is unavailable.",
        FileOperationRecoveryContentStatus.Inaccessible =>
            "The destination root or leaf could not be opened for stable read access.",
        FileOperationRecoveryContentStatus.Error =>
            string.IsNullOrWhiteSpace(readerMessage)
                ? "The destination content could not be verified reliably."
                : readerMessage,
        _ => readerMessage,
    };

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            TrimTrailingSeparators(left),
            TrimTrailingSeparators(right),
            StringComparison.OrdinalIgnoreCase);

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd('\\', '/');
}
