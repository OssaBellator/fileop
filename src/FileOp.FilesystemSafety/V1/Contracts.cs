using System.Collections.ObjectModel;

namespace FileOp.FilesystemSafety.V1;

/// <summary>
/// Stable identifiers and bounds for the first independently versioned FileOp filesystem-safety contract.
/// </summary>
public static class FileSystemSafetyContractV1
{
    public const string ContractId = "filesystem.safety.v1";
    public const int MajorVersion = 1;
    public const int MaxEntriesPerOperation = 256;
    public const string IdentityInspectCapability = "filesystem.identity.inspect.v1";
    public const string OperationPreflightCapability = "filesystem.operation.preflight.v1";
    public const string RecoveryAssessmentCapability = "filesystem.recovery.assess.v1";
    public const string EvidenceNotice = "Evidence only. This contract never grants filesystem mutation authority.";
}

public enum FileSystemEvidenceStateV1
{
    Available,
    Missing,
    Inaccessible,
    Error,
}

public enum FileSystemObjectKindV1
{
    Unknown,
    File,
    Directory,
}

/// <summary>
/// Stable Windows filesystem identity evidence captured from one open handle.
/// </summary>
public readonly record struct FileSystemIdentityV1(ulong VolumeSerialNumber, ulong FileReferenceNumber)
{
    public override string ToString() => $"{VolumeSerialNumber:X16}:{FileReferenceNumber:X16}";
}

/// <summary>
/// Current requested/canonical path and stable-identity evidence. CanonicalPath is handle-resolved for existing entries.
/// </summary>
public sealed record FileSystemPathEvidenceV1(
    string RequestedPath,
    string CanonicalPath,
    FileSystemEvidenceStateV1 State,
    FileSystemObjectKindV1 ObjectKind,
    bool IsLeafReparsePoint,
    FileSystemIdentityV1? Identity = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool Exists => State == FileSystemEvidenceStateV1.Available;

    public bool MutationAuthorized { get; }
}

public enum FileSystemOperationKindV1
{
    Copy,
    MoveSameVolume,
    PermanentDelete,
}

public enum FileSystemCollisionPolicyV1
{
    Ask,
    Skip,
    Stop,
}

public sealed record FileSystemOperationEntryRequestV1(
    string SourcePath,
    FileSystemObjectKindV1 ExpectedKind,
    FileSystemIdentityV1? ExpectedIdentity = null);

/// <summary>
/// Bounded, direct-child operation request used only to gather fresh safety evidence.
/// </summary>
public sealed record FileSystemOperationRequestV1
{
    public FileSystemOperationRequestV1(
        FileSystemOperationKindV1 kind,
        string sourceDirectoryPath,
        string? destinationDirectoryPath,
        IEnumerable<FileSystemOperationEntryRequestV1> entries,
        FileSystemCollisionPolicyV1 collisionPolicy = FileSystemCollisionPolicyV1.Ask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);
        ArgumentNullException.ThrowIfNull(entries);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (!Enum.IsDefined(collisionPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(collisionPolicy));
        }

        var snapshot = entries.ToArray();
        if (snapshot.Length is < 1 or > FileSystemSafetyContractV1.MaxEntriesPerOperation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(entries),
                $"Operation evidence requires 1-{FileSystemSafetyContractV1.MaxEntriesPerOperation} entries.");
        }
        if (snapshot.Any(static entry => entry is null))
        {
            throw new ArgumentException("Operation entries cannot contain null values.", nameof(entries));
        }
        if (kind != FileSystemOperationKindV1.PermanentDelete &&
            string.IsNullOrWhiteSpace(destinationDirectoryPath))
        {
            throw new ArgumentException("Copy and same-volume move evidence require a destination directory.", nameof(destinationDirectoryPath));
        }
        if (kind == FileSystemOperationKindV1.PermanentDelete &&
            !string.IsNullOrWhiteSpace(destinationDirectoryPath))
        {
            throw new ArgumentException("Permanent-delete evidence does not accept a destination directory.", nameof(destinationDirectoryPath));
        }

        Kind = kind;
        SourceDirectoryPath = sourceDirectoryPath;
        DestinationDirectoryPath = destinationDirectoryPath;
        Entries = Array.AsReadOnly(snapshot);
        CollisionPolicy = collisionPolicy;
    }

    public FileSystemOperationKindV1 Kind { get; }

    public string SourceDirectoryPath { get; }

    public string? DestinationDirectoryPath { get; }

    public IReadOnlyList<FileSystemOperationEntryRequestV1> Entries { get; }

    public FileSystemCollisionPolicyV1 CollisionPolicy { get; }
}

public enum FileSystemProtectedLocationDecisionV1
{
    OutsideProtectedTrees,
    Protected,
    Unclassified,
}

public sealed record FileSystemProtectedLocationEvidenceV1(
    FileSystemProtectedLocationDecisionV1 Decision,
    string Reason)
{
    public bool IsProtected => Decision == FileSystemProtectedLocationDecisionV1.Protected;
}

public enum FileSystemCollisionStateV1
{
    NotApplicable,
    None,
    ExistingFile,
    ExistingDirectory,
    ExistingReparsePoint,
    Unavailable,
}

public enum FileSystemOperationStrategyV1
{
    None,
    CopyCandidate,
    SameVolumeMoveCandidate,
    PermanentDeleteCandidate,
    CrossVolumeMoveUnsupported,
    DirectoryOperationUnsupported,
}

public enum FileSystemOperationItemDecisionV1
{
    ReadyForIndependentPolicyReview,
    Skip,
    NeedsCallerDecision,
    Blocked,
}

public sealed record FileSystemOperationItemEvidenceV1(
    int Ordinal,
    FileSystemOperationEntryRequestV1 Request,
    FileSystemPathEvidenceV1 Source,
    FileSystemPathEvidenceV1? Destination,
    FileSystemProtectedLocationEvidenceV1 SourceProtection,
    FileSystemProtectedLocationEvidenceV1? DestinationProtection,
    FileSystemCollisionStateV1 Collision,
    FileSystemOperationStrategyV1 Strategy,
    FileSystemOperationItemDecisionV1 Decision,
    bool? ExpectedIdentityMatches,
    string Message)
{
    public bool MutationAuthorized { get; }
}

public enum FileSystemOperationEvidenceStatusV1
{
    ReadyForIndependentPolicyReview,
    NeedsCallerDecision,
    Blocked,
}

/// <summary>
/// Fresh read-only operation evidence. A Ready status is deliberately not executable authority.
/// </summary>
public sealed record FileSystemOperationEvidenceV1(
    FileSystemOperationRequestV1 Request,
    FileSystemPathEvidenceV1 SourceDirectory,
    FileSystemPathEvidenceV1? DestinationDirectory,
    FileSystemProtectedLocationEvidenceV1 SourceDirectoryProtection,
    FileSystemProtectedLocationEvidenceV1? DestinationDirectoryProtection,
    IReadOnlyList<FileSystemOperationItemEvidenceV1> Items,
    FileSystemOperationEvidenceStatusV1 Status,
    string Summary)
{
    public bool MutationAuthorized { get; }
}

public enum FileSystemRecoveryEvidenceDimensionV1
{
    DestinationRoot,
    DestinationIdentity,
    MainStream,
    HardLinkCount,
    BasicMetadata,
    OwnerGroupDacl,
    NamedDataStreams,
}

public enum FileSystemRecoveryEvidenceDimensionStateV1
{
    Matches,
    Changed,
    Incomplete,
    Unavailable,
}

public sealed record FileSystemRecoveryDimensionEvidenceV1(
    FileSystemRecoveryEvidenceDimensionV1 Dimension,
    FileSystemRecoveryEvidenceDimensionStateV1 State);

/// <summary>
/// Caller-supplied durable-history/current-observation comparison states for deterministic recovery assessment.
/// Exactly one value for every V1 dimension is required.
/// </summary>
public sealed record FileSystemRecoveryAssessmentRequestV1
{
    public FileSystemRecoveryAssessmentRequestV1(
        Guid operationId,
        IEnumerable<FileSystemRecoveryDimensionEvidenceV1> dimensions)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Recovery assessment requires a non-empty operation ID.", nameof(operationId));
        }
        ArgumentNullException.ThrowIfNull(dimensions);
        var snapshot = dimensions.ToArray();
        var expected = Enum.GetValues<FileSystemRecoveryEvidenceDimensionV1>();
        if (snapshot.Length != expected.Length ||
            snapshot.Select(static item => item.Dimension).Distinct().Count() != expected.Length ||
            expected.Any(dimension => snapshot.All(item => item.Dimension != dimension)))
        {
            throw new ArgumentException("Recovery evidence must contain every V1 dimension exactly once.", nameof(dimensions));
        }
        if (snapshot.Any(static item => !Enum.IsDefined(item.Dimension) || !Enum.IsDefined(item.State)))
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }

        OperationId = operationId;
        Dimensions = Array.AsReadOnly(snapshot.OrderBy(static item => item.Dimension).ToArray());
    }

    public Guid OperationId { get; }

    public IReadOnlyList<FileSystemRecoveryDimensionEvidenceV1> Dimensions { get; }
}

public enum FileSystemRecoveryAssessmentStatusV1
{
    ObservedSubsetMatches,
    ObservedEvidenceChanged,
    EvidenceIncomplete,
    EvidenceUnavailable,
}

public sealed record FileSystemRecoveryAssessmentV1(
    Guid OperationId,
    IReadOnlyList<FileSystemRecoveryDimensionEvidenceV1> Dimensions,
    FileSystemRecoveryAssessmentStatusV1 Status,
    string Summary)
{
    public bool MutationAuthorized { get; }
}

/// <summary>
/// The complete V1 provider surface. It exposes inspection and assessment only; no mutation method exists.
/// </summary>
public interface IFileSystemSafetyProviderV1
{
    ValueTask<FileSystemPathEvidenceV1> InspectIdentityAsync(
        string path,
        CancellationToken cancellationToken = default);

    ValueTask<FileSystemOperationEvidenceV1> InspectOperationAsync(
        FileSystemOperationRequestV1 request,
        CancellationToken cancellationToken = default);

    FileSystemRecoveryAssessmentV1 AssessRecovery(FileSystemRecoveryAssessmentRequestV1 request);
}

public sealed record FileSystemFutureExecutionCapabilityV1(
    string CapabilityId,
    FileSystemOperationKindV1 Kind,
    bool Executable,
    string PromotionRequirement);

/// <summary>
/// Names only for future mutation-capable contracts. V1 intentionally maps them as non-executable.
/// </summary>
public static class FileSystemFutureExecutionCapabilitiesV1
{
    public static ReadOnlyCollection<FileSystemFutureExecutionCapabilityV1> All { get; } = Array.AsReadOnly(new FileSystemFutureExecutionCapabilityV1[]
    {
        new(
            "filesystem.copy.execute.v1",
            FileSystemOperationKindV1.Copy,
            Executable: false,
            "Requires independent consumer-boundary tests for confinement, writer lease, journal binding and unknown-outcome recovery."),
        new(
            "filesystem.move.same-volume.execute.v1",
            FileSystemOperationKindV1.MoveSameVolume,
            Executable: false,
            "Requires independent same-volume identity/lease tests plus consumer confinement and recovery equivalence."),
        new(
            "filesystem.delete.permanent.execute.v1",
            FileSystemOperationKindV1.PermanentDelete,
            Executable: false,
            "Requires independent destructive-boundary tests for exact identity, same-handle mutation, journaling and fail-closed recovery."),
    });
}
