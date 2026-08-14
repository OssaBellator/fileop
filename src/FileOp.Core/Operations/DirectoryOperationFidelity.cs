using System;
using System.Collections.Generic;

namespace FileOp.Core.Operations;

[Flags]
public enum DirectoryOperationFidelityFeature
{
    None = 0,
    ReparsePoint = 1 << 0,
    AlternateDataStreams = 1 << 1,
    NonDefaultSecurityDescriptor = 1 << 2,
    ExtendedAttributes = 1 << 3,
    SparseData = 1 << 4,
    CompressedData = 1 << 5,
    EncryptedData = 1 << 6,
    CaseSensitiveNamespace = 1 << 7,
    HardLinks = 1 << 8,
}

public sealed record DirectoryOperationFidelityEvidence(
    string CanonicalRootPath,
    DirectoryOperationFidelityFeature ObservedFeatures,
    bool EnumerationComplete,
    bool MetadataInspectionComplete);

public enum DirectoryOperationSupportState
{
    Unsupported,
    EvidenceIncomplete,
    EligibleForFuturePlainTreeExecutor,
}

public sealed record DirectoryOperationSupportClassification(
    DirectoryOperationSupportState State,
    string Summary,
    IReadOnlyList<DirectoryOperationFidelityFeature> BlockingFeatures)
{
    public bool CanEnterFutureMutationBoundary =>
        State == DirectoryOperationSupportState.EligibleForFuturePlainTreeExecutor;
}

/// <summary>
/// Defines the minimum fidelity contract a future directory Copy/Move executor must
/// meet. Classification is evidence only; no directory mutation primitive exists yet.
/// </summary>
public static class DirectoryOperationFidelityClassifier
{
    private const DirectoryOperationFidelityFeature UnsupportedFeatures =
        DirectoryOperationFidelityFeature.ReparsePoint |
        DirectoryOperationFidelityFeature.AlternateDataStreams |
        DirectoryOperationFidelityFeature.NonDefaultSecurityDescriptor |
        DirectoryOperationFidelityFeature.ExtendedAttributes |
        DirectoryOperationFidelityFeature.SparseData |
        DirectoryOperationFidelityFeature.CompressedData |
        DirectoryOperationFidelityFeature.EncryptedData |
        DirectoryOperationFidelityFeature.CaseSensitiveNamespace |
        DirectoryOperationFidelityFeature.HardLinks;

    public static DirectoryOperationSupportClassification Classify(
        DirectoryOperationFidelityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.CanonicalRootPath))
        {
            return new(
                DirectoryOperationSupportState.EvidenceIncomplete,
                "Directory operation fidelity evidence has no canonical root path.",
                Array.Empty<DirectoryOperationFidelityFeature>());
        }

        if (!evidence.EnumerationComplete || !evidence.MetadataInspectionComplete)
        {
            return new(
                DirectoryOperationSupportState.EvidenceIncomplete,
                "Directory Copy/Move remains disabled until recursive enumeration and metadata inspection complete without gaps.",
                Array.Empty<DirectoryOperationFidelityFeature>());
        }

        var blocked = evidence.ObservedFeatures & UnsupportedFeatures;
        if (blocked != DirectoryOperationFidelityFeature.None)
        {
            var features = new List<DirectoryOperationFidelityFeature>();
            foreach (DirectoryOperationFidelityFeature feature in Enum.GetValues<DirectoryOperationFidelityFeature>())
            {
                if (feature != DirectoryOperationFidelityFeature.None && blocked.HasFlag(feature))
                {
                    features.Add(feature);
                }
            }

            return new(
                DirectoryOperationSupportState.Unsupported,
                "Directory mutation is fail-closed because this tree contains filesystem semantics the current operation/recovery model cannot preserve exactly.",
                features.AsReadOnly());
        }

        return new(
            DirectoryOperationSupportState.EligibleForFuturePlainTreeExecutor,
            "This evidence describes a plain directory tree, but classification still grants no mutation authority; a recursive durable-history executor remains required.",
            Array.Empty<DirectoryOperationFidelityFeature>());
    }
}
