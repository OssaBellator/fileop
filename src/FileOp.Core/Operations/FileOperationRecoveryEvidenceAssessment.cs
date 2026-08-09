using System;
using System.Collections.Generic;
using System.Linq;

namespace FileOp.Core.Operations;

[Flags]
public enum FileOperationRecoveryEvidenceDimension
{
    None = 0,
    DestinationRoot = 1 << 0,
    DestinationIdentity = 1 << 1,
    MainStream = 1 << 2,
    HardLinkCount = 1 << 3,
    BasicMetadata = 1 << 4,
    OwnerGroupDacl = 1 << 5,
    AllObserved = DestinationRoot |
        DestinationIdentity |
        MainStream |
        HardLinkCount |
        BasicMetadata |
        OwnerGroupDacl,
}

public enum FileOperationRecoveryEvidenceDimensionState
{
    Matches,
    Changed,
    Incomplete,
    Unavailable,
}

public enum FileOperationRecoveryEvidenceAssessmentStatus
{
    ObservedSubsetMatches,
    ObservedEvidenceChanged,
    EvidenceIncomplete,
    EvidenceUnavailable,
}

/// <summary>
/// Read-only aggregation of the recovery evidence dimensions implemented today.
/// Even <see cref="FileOperationRecoveryEvidenceAssessmentStatus.ObservedSubsetMatches"/>
/// is evidence only: it does not prove the whole file was unchanged and grants no
/// deletion, replacement, recovery-mutation, or Undo authority.
/// </summary>
public sealed record FileOperationRecoveryEvidenceAssessmentItem
{
    public FileOperationRecoveryEvidenceAssessmentItem(
        int ordinal,
        FileOperationRecoveryInspectionItem inspection,
        FileOperationRecoveryEvidenceAssessmentStatus status,
        FileOperationRecoveryEvidenceDimension matchingDimensions,
        FileOperationRecoveryEvidenceDimension changedDimensions,
        FileOperationRecoveryEvidenceDimension incompleteDimensions,
        FileOperationRecoveryEvidenceDimension unavailableDimensions,
        string message)
    {
        Inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (ordinal != inspection.Ordinal)
        {
            throw new ArgumentException("Assessment ordinal must match its recovery inspection item.", nameof(ordinal));
        }

        ValidatePartition(
            matchingDimensions,
            changedDimensions,
            incompleteDimensions,
            unavailableDimensions);

        Ordinal = ordinal;
        Status = status;
        MatchingDimensions = matchingDimensions;
        ChangedDimensions = changedDimensions;
        IncompleteDimensions = incompleteDimensions;
        UnavailableDimensions = unavailableDimensions;
        Message = message;
    }

    public int Ordinal { get; }

    public FileOperationRecoveryInspectionItem Inspection { get; }

    public FileOperationRecoveryEvidenceAssessmentStatus Status { get; }

    public FileOperationRecoveryEvidenceDimension MatchingDimensions { get; }

    public FileOperationRecoveryEvidenceDimension ChangedDimensions { get; }

    public FileOperationRecoveryEvidenceDimension IncompleteDimensions { get; }

    public FileOperationRecoveryEvidenceDimension UnavailableDimensions { get; }

    public string Message { get; }

    public bool ObservedSubsetMatches =>
        Status == FileOperationRecoveryEvidenceAssessmentStatus.ObservedSubsetMatches;

    public FileOperationRecoveryEvidenceDimensionState GetDimensionState(
        FileOperationRecoveryEvidenceDimension dimension)
    {
        var numericDimension = (int)dimension;
        if (dimension == FileOperationRecoveryEvidenceDimension.None ||
            (dimension & FileOperationRecoveryEvidenceDimension.AllObserved) != dimension ||
            (numericDimension & (numericDimension - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimension),
                "Exactly one implemented recovery evidence dimension is required.");
        }

        if ((MatchingDimensions & dimension) != 0)
        {
            return FileOperationRecoveryEvidenceDimensionState.Matches;
        }

        if ((ChangedDimensions & dimension) != 0)
        {
            return FileOperationRecoveryEvidenceDimensionState.Changed;
        }

        if ((IncompleteDimensions & dimension) != 0)
        {
            return FileOperationRecoveryEvidenceDimensionState.Incomplete;
        }

        return FileOperationRecoveryEvidenceDimensionState.Unavailable;
    }

    private static void ValidatePartition(
        FileOperationRecoveryEvidenceDimension matching,
        FileOperationRecoveryEvidenceDimension changed,
        FileOperationRecoveryEvidenceDimension incomplete,
        FileOperationRecoveryEvidenceDimension unavailable)
    {
        var known = FileOperationRecoveryEvidenceDimension.AllObserved;
        var union = matching | changed | incomplete | unavailable;
        var overlap =
            (matching & changed) |
            (matching & incomplete) |
            (matching & unavailable) |
            (changed & incomplete) |
            (changed & unavailable) |
            (incomplete & unavailable);

        if ((union & ~known) != 0 || union != known || overlap != 0)
        {
            throw new ArgumentException(
                "Recovery evidence dimension masks must form one complete, non-overlapping partition of the implemented evidence dimensions.");
        }
    }
}

public sealed record FileOperationRecoveryEvidenceAssessment
{
    public FileOperationRecoveryEvidenceAssessment(
        Guid operationId,
        IEnumerable<FileOperationRecoveryEvidenceAssessmentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items.OrderBy(static item => item.Ordinal).ToArray();
        if (snapshot.Select(static item => item.Ordinal).Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException("Recovery evidence assessments require unique entry ordinals.", nameof(items));
        }

        OperationId = operationId;
        Items = Array.AsReadOnly(snapshot);
    }

    public Guid OperationId { get; }

    public IReadOnlyList<FileOperationRecoveryEvidenceAssessmentItem> Items { get; }

    public int MatchingObservedSubsetCount =>
        Items.Count(static item => item.ObservedSubsetMatches);

    public int ChangedCount =>
        Items.Count(static item =>
            item.Status == FileOperationRecoveryEvidenceAssessmentStatus.ObservedEvidenceChanged);

    public int IncompleteCount =>
        Items.Count(static item =>
            item.Status == FileOperationRecoveryEvidenceAssessmentStatus.EvidenceIncomplete);

    public int UnavailableCount =>
        Items.Count(static item =>
            item.Status == FileOperationRecoveryEvidenceAssessmentStatus.EvidenceUnavailable);
}

public static class FileOperationRecoveryEvidenceAssessor
{
    public static FileOperationRecoveryEvidenceAssessment Assess(
        FileOperationRecoveryInspection inspection,
        FileOperationRecoveryContentVerification content,
        FileOperationRecoveryBasicMetadataVerification basicMetadata,
        FileOperationRecoverySecurityDescriptorVerification securityDescriptor)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(basicMetadata);
        ArgumentNullException.ThrowIfNull(securityDescriptor);

        ValidateOperationId(inspection.OperationId, content.OperationId, nameof(content));
        ValidateOperationId(inspection.OperationId, basicMetadata.OperationId, nameof(basicMetadata));
        ValidateOperationId(inspection.OperationId, securityDescriptor.OperationId, nameof(securityDescriptor));

        var inspections = IndexByOrdinal(
            inspection.Items,
            static item => item.Ordinal,
            nameof(inspection));
        var contentItems = IndexByOrdinal(
            content.Items,
            static item => item.Ordinal,
            nameof(content));
        var metadataItems = IndexByOrdinal(
            basicMetadata.Items,
            static item => item.Ordinal,
            nameof(basicMetadata));
        var securityItems = IndexByOrdinal(
            securityDescriptor.Items,
            static item => item.Ordinal,
            nameof(securityDescriptor));

        RequireSameOrdinals(inspections, contentItems, nameof(content));
        RequireSameOrdinals(inspections, metadataItems, nameof(basicMetadata));
        RequireSameOrdinals(inspections, securityItems, nameof(securityDescriptor));

        var results = new List<FileOperationRecoveryEvidenceAssessmentItem>(inspections.Count);
        foreach (var ordinal in inspections.Keys.OrderBy(static ordinal => ordinal))
        {
            var inspectionItem = inspections[ordinal];
            var contentItem = contentItems[ordinal];
            var metadataItem = metadataItems[ordinal];
            var securityItem = securityItems[ordinal];

            RequireSameInspection(inspectionItem, contentItem.Inspection, nameof(content), ordinal);
            RequireSameInspection(inspectionItem, metadataItem.Inspection, nameof(basicMetadata), ordinal);
            RequireSameInspection(inspectionItem, securityItem.Inspection, nameof(securityDescriptor), ordinal);

            results.Add(AssessItem(
                inspection.DestinationDirectory,
                inspectionItem,
                contentItem,
                metadataItem,
                securityItem));
        }

        return new FileOperationRecoveryEvidenceAssessment(inspection.OperationId, results);
    }

    private static FileOperationRecoveryEvidenceAssessmentItem AssessItem(
        FileOperationRecoveryRootInspection root,
        FileOperationRecoveryInspectionItem inspection,
        FileOperationRecoveryContentVerificationItem content,
        FileOperationRecoveryBasicMetadataVerificationItem basicMetadata,
        FileOperationRecoverySecurityDescriptorVerificationItem securityDescriptor)
    {
        var matching = FileOperationRecoveryEvidenceDimension.None;
        var changed = FileOperationRecoveryEvidenceDimension.None;
        var incomplete = FileOperationRecoveryEvidenceDimension.None;
        var unavailable = FileOperationRecoveryEvidenceDimension.None;

        Add(
            FileOperationRecoveryEvidenceDimension.DestinationRoot,
            ClassifyRoot(root.Status),
            ref matching,
            ref changed,
            ref incomplete,
            ref unavailable);
        Add(
            FileOperationRecoveryEvidenceDimension.DestinationIdentity,
            ClassifyDestination(inspection.Status),
            ref matching,
            ref changed,
            ref incomplete,
            ref unavailable);
        Add(
            FileOperationRecoveryEvidenceDimension.MainStream,
            ClassifyMainStream(content.Status),
            ref matching,
            ref changed,
            ref incomplete,
            ref unavailable);
        Add(
            FileOperationRecoveryEvidenceDimension.HardLinkCount,
            ClassifyHardLink(content.HardLinkStatus),
            ref matching,
            ref changed,
            ref incomplete,
            ref unavailable);
        Add(
            FileOperationRecoveryEvidenceDimension.BasicMetadata,
            ClassifyBasicMetadata(basicMetadata.Comparison.Status),
            ref matching,
            ref changed,
            ref incomplete,
            ref unavailable);
        Add(
            FileOperationRecoveryEvidenceDimension.OwnerGroupDacl,
            ClassifySecurityDescriptor(securityDescriptor.Comparison.Status),
            ref matching,
            ref changed,
            ref incomplete,
            ref unavailable);

        var status = changed != FileOperationRecoveryEvidenceDimension.None
            ? FileOperationRecoveryEvidenceAssessmentStatus.ObservedEvidenceChanged
            : unavailable != FileOperationRecoveryEvidenceDimension.None
                ? FileOperationRecoveryEvidenceAssessmentStatus.EvidenceUnavailable
                : incomplete != FileOperationRecoveryEvidenceDimension.None
                    ? FileOperationRecoveryEvidenceAssessmentStatus.EvidenceIncomplete
                    : FileOperationRecoveryEvidenceAssessmentStatus.ObservedSubsetMatches;

        return new FileOperationRecoveryEvidenceAssessmentItem(
            inspection.Ordinal,
            inspection,
            status,
            matching,
            changed,
            incomplete,
            unavailable,
            Describe(status));
    }

    private static FileOperationRecoveryEvidenceDimensionState ClassifyRoot(
        FileOperationRecoveryRootStatus status) => status switch
        {
            FileOperationRecoveryRootStatus.SameObject => FileOperationRecoveryEvidenceDimensionState.Matches,
            FileOperationRecoveryRootStatus.NoVerifiedIdentity => FileOperationRecoveryEvidenceDimensionState.Incomplete,
            FileOperationRecoveryRootStatus.Inaccessible or
                FileOperationRecoveryRootStatus.Error => FileOperationRecoveryEvidenceDimensionState.Unavailable,
            FileOperationRecoveryRootStatus.Missing or
                FileOperationRecoveryRootStatus.DifferentObject or
                FileOperationRecoveryRootStatus.Redirected or
                FileOperationRecoveryRootStatus.ReparsePoint or
                FileOperationRecoveryRootStatus.UnexpectedType => FileOperationRecoveryEvidenceDimensionState.Changed,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static FileOperationRecoveryEvidenceDimensionState ClassifyDestination(
        FileOperationRecoveryDestinationStatus status) => status switch
        {
            FileOperationRecoveryDestinationStatus.SameObject => FileOperationRecoveryEvidenceDimensionState.Matches,
            FileOperationRecoveryDestinationStatus.NoVerifiedIdentity => FileOperationRecoveryEvidenceDimensionState.Incomplete,
            FileOperationRecoveryDestinationStatus.Inaccessible or
                FileOperationRecoveryDestinationStatus.Error => FileOperationRecoveryEvidenceDimensionState.Unavailable,
            FileOperationRecoveryDestinationStatus.Missing or
                FileOperationRecoveryDestinationStatus.DifferentObject or
                FileOperationRecoveryDestinationStatus.Redirected or
                FileOperationRecoveryDestinationStatus.ReparsePoint or
                FileOperationRecoveryDestinationStatus.UnexpectedType => FileOperationRecoveryEvidenceDimensionState.Changed,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static FileOperationRecoveryEvidenceDimensionState ClassifyMainStream(
        FileOperationRecoveryContentStatus status) => status switch
        {
            FileOperationRecoveryContentStatus.MatchesRecordedMainStream => FileOperationRecoveryEvidenceDimensionState.Matches,
            FileOperationRecoveryContentStatus.NoRecordedFingerprint => FileOperationRecoveryEvidenceDimensionState.Incomplete,
            FileOperationRecoveryContentStatus.DifferentMainStream or
                FileOperationRecoveryContentStatus.Missing or
                FileOperationRecoveryContentStatus.DifferentObject or
                FileOperationRecoveryContentStatus.Redirected or
                FileOperationRecoveryContentStatus.ReparsePoint or
                FileOperationRecoveryContentStatus.UnexpectedType or
                FileOperationRecoveryContentStatus.DestinationRootChanged => FileOperationRecoveryEvidenceDimensionState.Changed,
            FileOperationRecoveryContentStatus.DestinationRootNotVerified or
                FileOperationRecoveryContentStatus.NotSameRecordedObject or
                FileOperationRecoveryContentStatus.Busy or
                FileOperationRecoveryContentStatus.Inaccessible or
                FileOperationRecoveryContentStatus.Error => FileOperationRecoveryEvidenceDimensionState.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static FileOperationRecoveryEvidenceDimensionState ClassifyHardLink(
        FileOperationRecoveryHardLinkStatus status) => status switch
        {
            FileOperationRecoveryHardLinkStatus.SameCount => FileOperationRecoveryEvidenceDimensionState.Matches,
            FileOperationRecoveryHardLinkStatus.DifferentCount => FileOperationRecoveryEvidenceDimensionState.Changed,
            FileOperationRecoveryHardLinkStatus.NoRecordedCount => FileOperationRecoveryEvidenceDimensionState.Incomplete,
            FileOperationRecoveryHardLinkStatus.Unavailable => FileOperationRecoveryEvidenceDimensionState.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static FileOperationRecoveryEvidenceDimensionState ClassifyBasicMetadata(
        FileOperationRecoveryBasicMetadataStatus status) => status switch
        {
            FileOperationRecoveryBasicMetadataStatus.SameStableMetadata => FileOperationRecoveryEvidenceDimensionState.Matches,
            FileOperationRecoveryBasicMetadataStatus.DifferentStableMetadata => FileOperationRecoveryEvidenceDimensionState.Changed,
            FileOperationRecoveryBasicMetadataStatus.NoRecordedEvidence => FileOperationRecoveryEvidenceDimensionState.Incomplete,
            FileOperationRecoveryBasicMetadataStatus.Unavailable => FileOperationRecoveryEvidenceDimensionState.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static FileOperationRecoveryEvidenceDimensionState ClassifySecurityDescriptor(
        FileOperationRecoverySecurityDescriptorStatus status) => status switch
        {
            FileOperationRecoverySecurityDescriptorStatus.SameQueriedDescriptorBytes => FileOperationRecoveryEvidenceDimensionState.Matches,
            FileOperationRecoverySecurityDescriptorStatus.DifferentQueriedDescriptorBytes => FileOperationRecoveryEvidenceDimensionState.Changed,
            FileOperationRecoverySecurityDescriptorStatus.NoRecordedEvidence => FileOperationRecoveryEvidenceDimensionState.Incomplete,
            FileOperationRecoverySecurityDescriptorStatus.Unavailable => FileOperationRecoveryEvidenceDimensionState.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

    private static void Add(
        FileOperationRecoveryEvidenceDimension dimension,
        FileOperationRecoveryEvidenceDimensionState state,
        ref FileOperationRecoveryEvidenceDimension matching,
        ref FileOperationRecoveryEvidenceDimension changed,
        ref FileOperationRecoveryEvidenceDimension incomplete,
        ref FileOperationRecoveryEvidenceDimension unavailable)
    {
        switch (state)
        {
            case FileOperationRecoveryEvidenceDimensionState.Matches:
                matching |= dimension;
                break;
            case FileOperationRecoveryEvidenceDimensionState.Changed:
                changed |= dimension;
                break;
            case FileOperationRecoveryEvidenceDimensionState.Incomplete:
                incomplete |= dimension;
                break;
            case FileOperationRecoveryEvidenceDimensionState.Unavailable:
                unavailable |= dimension;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static Dictionary<int, T> IndexByOrdinal<T>(
        IEnumerable<T> items,
        Func<T, int> getOrdinal,
        string parameterName)
    {
        var result = new Dictionary<int, T>();
        foreach (var item in items)
        {
            var ordinal = getOrdinal(item);
            if (ordinal < 0 || !result.TryAdd(ordinal, item))
            {
                throw new ArgumentException(
                    $"{parameterName} must contain unique non-negative entry ordinals.",
                    parameterName);
            }
        }

        return result;
    }

    private static void RequireSameOrdinals<T>(
        IReadOnlyDictionary<int, FileOperationRecoveryInspectionItem> inspections,
        IReadOnlyDictionary<int, T> verification,
        string parameterName)
    {
        if (inspections.Count != verification.Count ||
            inspections.Keys.Any(ordinal => !verification.ContainsKey(ordinal)))
        {
            throw new ArgumentException(
                "Recovery verification sets must contain exactly the same entry ordinals as the recovery inspection.",
                parameterName);
        }
    }

    private static void RequireSameInspection(
        FileOperationRecoveryInspectionItem expected,
        FileOperationRecoveryInspectionItem actual,
        string parameterName,
        int ordinal)
    {
        if (expected != actual)
        {
            throw new ArgumentException(
                $"Recovery verification item {ordinal} does not correspond to the supplied recovery inspection snapshot.",
                parameterName);
        }
    }

    private static void ValidateOperationId(Guid expected, Guid actual, string parameterName)
    {
        if (actual != expected)
        {
            throw new ArgumentException(
                "Recovery verification results must belong to the same operation as the supplied recovery inspection.",
                parameterName);
        }
    }

    private static string Describe(FileOperationRecoveryEvidenceAssessmentStatus status) => status switch
    {
        FileOperationRecoveryEvidenceAssessmentStatus.ObservedSubsetMatches =>
            "All recovery evidence dimensions implemented by this assessor match their durable observations. This is only an observed-subset match and grants no mutation authority.",
        FileOperationRecoveryEvidenceAssessmentStatus.ObservedEvidenceChanged =>
            "At least one implemented recovery evidence dimension differs from its durable observation; the dimension masks identify the changed evidence.",
        FileOperationRecoveryEvidenceAssessmentStatus.EvidenceIncomplete =>
            "No implemented evidence dimension is known to differ, but durable history lacks at least one evidence dimension required by this assessor.",
        FileOperationRecoveryEvidenceAssessmentStatus.EvidenceUnavailable =>
            "No implemented evidence dimension is known to differ, but at least one current evidence dimension could not be verified reliably.",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
