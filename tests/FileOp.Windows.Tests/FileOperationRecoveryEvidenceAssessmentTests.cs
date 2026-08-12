using System;
using System.Collections.Generic;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationRecoveryEvidenceAssessmentTests
{
    private const string RootPath = @"D:\Real\Destination";
    private static readonly FileIdentity RootIdentity = new(5, 50);
    private static readonly FileIdentity LeafIdentity = new(6, 60);
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "abababababababababababababababababababababababababababababababab");
    private static readonly FileBasicMetadataEvidence Metadata = new(100, 200, 300, 0x2021);
    private static readonly FileSecurityDescriptorEvidence Security = new(
        FileSecurityDescriptorEvidence.QueriedSecurityInformationMask,
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");
    private static readonly FileNamedDataStreamTopologyEvidence NamedStreams = new(
        1,
        1,
        "efefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefef");

    [TestMethod]
    public void AllImplementedEvidenceMatchesWithoutGrantingUndoAuthority()
    {
        var input = CreateMatchingInput();

        var assessment = FileOperationRecoveryEvidenceAssessor.Assess(
            input.Inspection,
            input.Content,
            input.BasicMetadata,
            input.SecurityDescriptor,
            input.NamedDataStreams);

        var item = assessment.Items[0];
        Assert.AreEqual(FileOperationRecoveryEvidenceAssessmentStatus.ObservedSubsetMatches, item.Status);
        Assert.AreEqual(FileOperationRecoveryEvidenceDimension.AllObserved, item.MatchingDimensions);
        Assert.AreEqual(FileOperationRecoveryEvidenceDimension.None, item.ChangedDimensions);
        Assert.AreEqual(FileOperationRecoveryEvidenceDimension.None, item.IncompleteDimensions);
        Assert.AreEqual(FileOperationRecoveryEvidenceDimension.None, item.UnavailableDimensions);
        Assert.AreEqual(1, assessment.MatchingObservedSubsetCount);
        Assert.AreEqual(FileOperationUndoKind.None, item.Inspection.Entry.UndoKind);
        Assert.IsFalse(item.Inspection.Entry.IsUndoCandidate);
        StringAssert.Contains(item.Message, "no mutation authority");
        StringAssert.Contains(item.Message, "not named-stream contents");

        foreach (var dimension in new[]
        {
            FileOperationRecoveryEvidenceDimension.DestinationRoot,
            FileOperationRecoveryEvidenceDimension.DestinationIdentity,
            FileOperationRecoveryEvidenceDimension.MainStream,
            FileOperationRecoveryEvidenceDimension.HardLinkCount,
            FileOperationRecoveryEvidenceDimension.BasicMetadata,
            FileOperationRecoveryEvidenceDimension.OwnerGroupDacl,
            FileOperationRecoveryEvidenceDimension.NamedDataStreams,
        })
        {
            Assert.AreEqual(FileOperationRecoveryEvidenceDimensionState.Matches, item.GetDimensionState(dimension));
        }
    }

    [TestMethod]
    public void ChangedEvidenceWinsOverUnavailableAndIncompleteEvidence()
    {
        var input = CreateMatchingInput();
        var contentItem = input.Content.Items[0] with { CurrentDestinationHardLinkCount = 3 };
        var content = new FileOperationRecoveryContentVerification(input.Inspection.OperationId, new[] { contentItem });
        var metadataItem = input.BasicMetadata.Items[0] with
        {
            Comparison = new FileOperationRecoveryBasicMetadataComparison(
                FileOperationRecoveryBasicMetadataStatus.Unavailable,
                Metadata,
                Current: null,
                CreationTimeMatches: null,
                LastWriteTimeMatches: null,
                StableCopiedAttributesMatch: null,
                LastAccessTimeMatchesDiagnostic: null),
        };
        var metadata = new FileOperationRecoveryBasicMetadataVerification(
            input.Inspection.OperationId,
            new[] { metadataItem });
        var securityItem = input.SecurityDescriptor.Items[0] with
        {
            Comparison = FileOperationRecoverySecurityDescriptorComparer.Compare(null, null),
        };
        var security = new FileOperationRecoverySecurityDescriptorVerification(
            input.Inspection.OperationId,
            new[] { securityItem });

        var item = FileOperationRecoveryEvidenceAssessor.Assess(
            input.Inspection,
            content,
            metadata,
            security,
            input.NamedDataStreams).Items[0];

        Assert.AreEqual(FileOperationRecoveryEvidenceAssessmentStatus.ObservedEvidenceChanged, item.Status);
        Assert.AreEqual(FileOperationRecoveryEvidenceDimensionState.Changed,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.HardLinkCount));
        Assert.AreEqual(FileOperationRecoveryEvidenceDimensionState.Unavailable,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.BasicMetadata));
        Assert.AreEqual(FileOperationRecoveryEvidenceDimensionState.Incomplete,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.OwnerGroupDacl));
    }

    [TestMethod]
    public void NamedStreamTopologyDifferenceIsAChangedDimension()
    {
        var input = CreateMatchingInput();
        var changed = input.NamedDataStreams.Items[0] with
        {
            Comparison = FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(
                NamedStreams,
                new FileNamedDataStreamTopologyEvidence(
                    1,
                    2,
                    "1212121212121212121212121212121212121212121212121212121212121212")),
        };
        var namedStreams = new FileOperationRecoveryNamedDataStreamTopologyVerification(
            input.Inspection.OperationId,
            new[] { changed });

        var item = FileOperationRecoveryEvidenceAssessor.Assess(
            input.Inspection,
            input.Content,
            input.BasicMetadata,
            input.SecurityDescriptor,
            namedStreams).Items[0];

        Assert.AreEqual(FileOperationRecoveryEvidenceAssessmentStatus.ObservedEvidenceChanged, item.Status);
        Assert.AreEqual(FileOperationRecoveryEvidenceDimensionState.Changed,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.NamedDataStreams));
    }

    [TestMethod]
    public void UnavailableEvidenceWinsOverIncompleteEvidenceWhenNothingChanged()
    {
        var input = CreateMatchingInput();
        var contentItem = input.Content.Items[0] with
        {
            Status = FileOperationRecoveryContentStatus.Busy,
            CurrentContentFingerprint = null,
        };
        var content = new FileOperationRecoveryContentVerification(input.Inspection.OperationId, new[] { contentItem });
        var metadataItem = input.BasicMetadata.Items[0] with
        {
            Comparison = FileOperationRecoveryBasicMetadataComparer.Compare(null, null),
        };
        var metadata = new FileOperationRecoveryBasicMetadataVerification(input.Inspection.OperationId, new[] { metadataItem });

        var item = FileOperationRecoveryEvidenceAssessor.Assess(
            input.Inspection,
            content,
            metadata,
            input.SecurityDescriptor,
            input.NamedDataStreams).Items[0];

        Assert.AreEqual(FileOperationRecoveryEvidenceAssessmentStatus.EvidenceUnavailable, item.Status);
        Assert.AreEqual(FileOperationRecoveryEvidenceDimensionState.Unavailable,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.MainStream));
        Assert.AreEqual(FileOperationRecoveryEvidenceDimensionState.Incomplete,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.BasicMetadata));
    }

    [TestMethod]
    public void MissingRecordedDimensionProducesIncompleteAssessment()
    {
        var input = CreateMatchingInput();
        var securityItem = input.SecurityDescriptor.Items[0] with
        {
            Comparison = FileOperationRecoverySecurityDescriptorComparer.Compare(null, null),
        };
        var security = new FileOperationRecoverySecurityDescriptorVerification(input.Inspection.OperationId, new[] { securityItem });

        var item = FileOperationRecoveryEvidenceAssessor.Assess(
            input.Inspection,
            input.Content,
            input.BasicMetadata,
            security,
            input.NamedDataStreams).Items[0];

        Assert.AreEqual(FileOperationRecoveryEvidenceAssessmentStatus.EvidenceIncomplete, item.Status);
        Assert.AreEqual(FileOperationRecoveryEvidenceDimensionState.Incomplete,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.OwnerGroupDacl));
        Assert.AreEqual(
            FileOperationRecoveryEvidenceDimension.AllObserved & ~FileOperationRecoveryEvidenceDimension.OwnerGroupDacl,
            item.MatchingDimensions);
    }

    [TestMethod]
    public void AssessorRejectsOperationOrdinalAndInspectionSnapshotMismatch()
    {
        var input = CreateMatchingInput();
        var wrongOperation = new FileOperationRecoveryContentVerification(Guid.NewGuid(), input.Content.Items);
        Assert.Throws<ArgumentException>(() =>
            FileOperationRecoveryEvidenceAssessor.Assess(
                input.Inspection,
                wrongOperation,
                input.BasicMetadata,
                input.SecurityDescriptor,
                input.NamedDataStreams));

        var missingNamedStreams = new FileOperationRecoveryNamedDataStreamTopologyVerification(
            input.Inspection.OperationId,
            Array.Empty<FileOperationRecoveryNamedDataStreamTopologyVerificationItem>());
        Assert.Throws<ArgumentException>(() =>
            FileOperationRecoveryEvidenceAssessor.Assess(
                input.Inspection,
                input.Content,
                input.BasicMetadata,
                input.SecurityDescriptor,
                missingNamedStreams));

        var alteredInspection = input.Inspection.Items[0] with { Message = "altered snapshot" };
        var alteredTopologyItem = input.NamedDataStreams.Items[0] with { Inspection = alteredInspection };
        var alteredTopology = new FileOperationRecoveryNamedDataStreamTopologyVerification(
            input.Inspection.OperationId,
            new[] { alteredTopologyItem });
        Assert.Throws<ArgumentException>(() =>
            FileOperationRecoveryEvidenceAssessor.Assess(
                input.Inspection,
                input.Content,
                input.BasicMetadata,
                input.SecurityDescriptor,
                alteredTopology));
    }

    [TestMethod]
    public void AssessmentDefensivelySnapshotsAndSortsItems()
    {
        var later = CreateDirectAssessmentItem(1);
        var earlier = CreateDirectAssessmentItem(0);
        var source = new List<FileOperationRecoveryEvidenceAssessmentItem> { later, earlier };

        var assessment = new FileOperationRecoveryEvidenceAssessment(Guid.NewGuid(), source);
        source.Clear();

        Assert.AreEqual(2, assessment.Items.Count);
        Assert.AreEqual(0, assessment.Items[0].Ordinal);
        Assert.AreEqual(1, assessment.Items[1].Ordinal);
        Assert.Throws<ArgumentException>(() =>
            new FileOperationRecoveryEvidenceAssessment(Guid.NewGuid(), new[] { earlier, earlier }));
    }

    private static MatchingInput CreateMatchingInput()
    {
        var operationId = Guid.NewGuid();
        var inspectionItem = CreateInspectionItem(0);
        var root = new FileOperationRecoveryRootInspection(
            RootIdentity,
            FileOperationRecoveryRootStatus.SameObject,
            ExistingDirectory(RootPath, RootIdentity),
            "same root",
            RootPath);
        var inspection = new FileOperationRecoveryInspection(operationId, new[] { inspectionItem }, root);
        var contentItem = new FileOperationRecoveryContentVerificationItem(
            0,
            inspectionItem,
            FileOperationRecoveryContentStatus.MatchesRecordedMainStream,
            Fingerprint,
            "matching main stream")
        {
            CurrentDestinationHardLinkCount = 2,
        };
        var content = new FileOperationRecoveryContentVerification(operationId, new[] { contentItem });
        var metadataItem = new FileOperationRecoveryBasicMetadataVerificationItem(
            0,
            inspectionItem,
            FileContentFingerprintReadStatus.Success,
            FileOperationRecoveryBasicMetadataComparer.Compare(Metadata, Metadata),
            "matching metadata");
        var metadata = new FileOperationRecoveryBasicMetadataVerification(operationId, new[] { metadataItem });
        var securityItem = new FileOperationRecoverySecurityDescriptorVerificationItem(
            0,
            inspectionItem,
            FileContentFingerprintReadStatus.Success,
            FileOperationRecoverySecurityDescriptorComparer.Compare(Security, Security),
            "matching security");
        var security = new FileOperationRecoverySecurityDescriptorVerification(operationId, new[] { securityItem });
        var streamItem = new FileOperationRecoveryNamedDataStreamTopologyVerificationItem(
            0,
            inspectionItem,
            FileContentFingerprintReadStatus.Success,
            FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(NamedStreams, NamedStreams),
            "matching named streams");
        var streams = new FileOperationRecoveryNamedDataStreamTopologyVerification(operationId, new[] { streamItem });
        return new MatchingInput(inspection, content, metadata, security, streams);
    }

    private static FileOperationRecoveryEvidenceAssessmentItem CreateDirectAssessmentItem(int ordinal)
    {
        var inspection = CreateInspectionItem(ordinal);
        return new FileOperationRecoveryEvidenceAssessmentItem(
            ordinal,
            inspection,
            FileOperationRecoveryEvidenceAssessmentStatus.ObservedSubsetMatches,
            FileOperationRecoveryEvidenceDimension.AllObserved,
            FileOperationRecoveryEvidenceDimension.None,
            FileOperationRecoveryEvidenceDimension.None,
            FileOperationRecoveryEvidenceDimension.None,
            "observed subset matches; no mutation authority");
    }

    private static FileOperationRecoveryInspectionItem CreateInspectionItem(int ordinal)
    {
        var path = RootPath + $@"\payload-{ordinal}.bin";
        var now = DateTimeOffset.UtcNow;
        var entry = new FileOperationActionEntry(
            ordinal,
            new FileOperationEntry($@"C:\Source\payload-{ordinal}.bin", $"payload-{ordinal}.bin", false),
            $@"C:\Real\Source\payload-{ordinal}.bin",
            path,
            FileOperationActionEntryState.RecoveryRequired,
            now,
            now,
            new FileIdentity(1, unchecked((ulong)(10 + ordinal))),
            LeafIdentity,
            FileOperationUndoKind.None,
            new FileOperationFailure("RecoveryRequired", "fixture", path, false),
            Fingerprint)
        {
            DestinationHardLinkCount = 2,
        };
        return new FileOperationRecoveryInspectionItem(
            ordinal,
            entry,
            FileOperationRecoveryDestinationStatus.SameObject,
            ExistingFile(path, LeafIdentity),
            "same leaf");
    }

    private static FileOperationCanonicalPath ExistingFile(string path, FileIdentity identity) =>
        new(path, path, FileOperationCanonicalPathState.File, false, identity);

    private static FileOperationCanonicalPath ExistingDirectory(string path, FileIdentity identity) =>
        new(path, path, FileOperationCanonicalPathState.Directory, false, identity);

    private sealed record MatchingInput(
        FileOperationRecoveryInspection Inspection,
        FileOperationRecoveryContentVerification Content,
        FileOperationRecoveryBasicMetadataVerification BasicMetadata,
        FileOperationRecoverySecurityDescriptorVerification SecurityDescriptor,
        FileOperationRecoveryNamedDataStreamTopologyVerification NamedDataStreams);
}
