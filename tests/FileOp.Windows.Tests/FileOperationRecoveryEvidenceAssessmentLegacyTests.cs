using System;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationRecoveryEvidenceAssessmentLegacyTests
{
    private static readonly FileIdentity LeafIdentity = new(6, 60);
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "abababababababababababababababababababababababababababababababab");
    private static readonly FileBasicMetadataEvidence Metadata = new(100, 200, 300, 0x20);
    private static readonly FileSecurityDescriptorEvidence Security = new(
        FileSecurityDescriptorEvidence.QueriedSecurityInformationMask,
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");

    [TestMethod]
    public void LegacyMissingRootIdentityMakesBlockedMainStreamIncomplete()
    {
        var operationId = Guid.NewGuid();
        var path = @"D:\Real\Destination\payload.bin";
        var now = DateTimeOffset.UtcNow;
        var entry = new FileOperationActionEntry(
            0,
            new FileOperationEntry(@"C:\Source\payload.bin", "payload.bin", false),
            @"C:\Real\Source\payload.bin",
            path,
            FileOperationActionEntryState.RecoveryRequired,
            now,
            now,
            new FileIdentity(1, 10),
            LeafIdentity,
            FileOperationUndoKind.None,
            new FileOperationFailure("RecoveryRequired", "fixture", path, false),
            Fingerprint)
        {
            DestinationHardLinkCount = 2,
        };
        var inspectionItem = new FileOperationRecoveryInspectionItem(
            0,
            entry,
            FileOperationRecoveryDestinationStatus.SameObject,
            ExistingFile(path, LeafIdentity),
            "same leaf");
        var inspection = new FileOperationRecoveryInspection(
            operationId,
            new[] { inspectionItem },
            new FileOperationRecoveryRootInspection(
                RecordedIdentity: null,
                FileOperationRecoveryRootStatus.NoVerifiedIdentity,
                CurrentDirectory: null,
                "legacy root identity missing",
                @"D:\Real\Destination"));
        var content = new FileOperationRecoveryContentVerification(
            operationId,
            new[]
            {
                new FileOperationRecoveryContentVerificationItem(
                    0,
                    inspectionItem,
                    FileOperationRecoveryContentStatus.DestinationRootNotVerified,
                    CurrentContentFingerprint: null,
                    "root not verified")
                {
                    CurrentDestinationHardLinkCount = 2,
                },
            });
        var metadata = new FileOperationRecoveryBasicMetadataVerification(
            operationId,
            new[]
            {
                new FileOperationRecoveryBasicMetadataVerificationItem(
                    0,
                    inspectionItem,
                    FileContentFingerprintReadStatus.Success,
                    FileOperationRecoveryBasicMetadataComparer.Compare(Metadata, Metadata),
                    "matching metadata"),
            });
        var security = new FileOperationRecoverySecurityDescriptorVerification(
            operationId,
            new[]
            {
                new FileOperationRecoverySecurityDescriptorVerificationItem(
                    0,
                    inspectionItem,
                    FileContentFingerprintReadStatus.Success,
                    FileOperationRecoverySecurityDescriptorComparer.Compare(Security, Security),
                    "matching security"),
            });

        var item = FileOperationRecoveryEvidenceAssessor.Assess(
            inspection,
            content,
            metadata,
            security).Items[0];

        Assert.AreEqual(FileOperationRecoveryEvidenceAssessmentStatus.EvidenceIncomplete, item.Status);
        Assert.AreEqual(
            FileOperationRecoveryEvidenceDimensionState.Incomplete,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.DestinationRoot));
        Assert.AreEqual(
            FileOperationRecoveryEvidenceDimensionState.Incomplete,
            item.GetDimensionState(FileOperationRecoveryEvidenceDimension.MainStream));
        Assert.AreEqual(FileOperationRecoveryEvidenceDimension.None, item.UnavailableDimensions);
    }

    [TestMethod]
    public void AssessmentItemRejectsStatusThatContradictsDimensionMasks()
    {
        var inspection = CreateSameObjectInspection();
        Assert.ThrowsException<ArgumentException>(() =>
            new FileOperationRecoveryEvidenceAssessmentItem(
                0,
                inspection,
                FileOperationRecoveryEvidenceAssessmentStatus.ObservedSubsetMatches,
                FileOperationRecoveryEvidenceDimension.AllObserved & ~FileOperationRecoveryEvidenceDimension.HardLinkCount,
                FileOperationRecoveryEvidenceDimension.HardLinkCount,
                FileOperationRecoveryEvidenceDimension.None,
                FileOperationRecoveryEvidenceDimension.None,
                "contradictory fixture"));
    }

    private static FileOperationRecoveryInspectionItem CreateSameObjectInspection()
    {
        var path = @"D:\Real\Destination\payload.bin";
        var now = DateTimeOffset.UtcNow;
        var entry = new FileOperationActionEntry(
            0,
            new FileOperationEntry(@"C:\Source\payload.bin", "payload.bin", false),
            @"C:\Real\Source\payload.bin",
            path,
            FileOperationActionEntryState.RecoveryRequired,
            now,
            now,
            new FileIdentity(1, 10),
            LeafIdentity,
            FileOperationUndoKind.None,
            new FileOperationFailure("RecoveryRequired", "fixture", path, false),
            Fingerprint)
        {
            DestinationHardLinkCount = 2,
        };
        return new FileOperationRecoveryInspectionItem(
            0,
            entry,
            FileOperationRecoveryDestinationStatus.SameObject,
            ExistingFile(path, LeafIdentity),
            "same leaf");
    }

    private static FileOperationCanonicalPath ExistingFile(string path, FileIdentity identity) =>
        new(path, path, FileOperationCanonicalPathState.File, false, identity);
}
