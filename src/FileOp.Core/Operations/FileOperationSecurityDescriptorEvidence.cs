using System;

namespace FileOp.Core.Operations;

/// <summary>
/// SHA-256 evidence for the exact self-relative security-descriptor bytes returned
/// for the requested owner/group/DACL subset. Raw SID/ACL bytes are never persisted.
/// </summary>
public sealed record FileSecurityDescriptorEvidence
{
    public const uint OwnerSecurityInformation = 0x00000001u;
    public const uint GroupSecurityInformation = 0x00000002u;
    public const uint DaclSecurityInformation = 0x00000004u;
    public const uint QueriedSecurityInformationMask =
        OwnerSecurityInformation |
        GroupSecurityInformation |
        DaclSecurityInformation;

    public FileSecurityDescriptorEvidence(uint securityInformation, string sha256HexDigest)
    {
        if (securityInformation != QueriedSecurityInformationMask)
        {
            throw new ArgumentOutOfRangeException(
                nameof(securityInformation),
                $"Copy recovery security evidence must query exactly owner/group/DACL (0x{QueriedSecurityInformationMask:X8}).");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sha256HexDigest);
        if (sha256HexDigest.Length != 64 || !IsHex(sha256HexDigest))
        {
            throw new ArgumentException(
                "Security-descriptor SHA-256 evidence must contain exactly 64 hexadecimal characters.",
                nameof(sha256HexDigest));
        }

        SecurityInformation = securityInformation;
        Sha256HexDigest = sha256HexDigest.ToLowerInvariant();
    }

    public uint SecurityInformation { get; }

    public string Sha256HexDigest { get; }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}

public enum FileOperationRecoverySecurityDescriptorStatus
{
    NoRecordedEvidence,
    SameQueriedDescriptorBytes,
    DifferentQueriedDescriptorBytes,
    Unavailable,
}

public sealed record FileOperationRecoverySecurityDescriptorComparison(
    FileOperationRecoverySecurityDescriptorStatus Status,
    FileSecurityDescriptorEvidence? Recorded,
    FileSecurityDescriptorEvidence? Current,
    bool? SecurityInformationMaskMatches,
    bool? Sha256Matches)
{
    public bool SameQueriedDescriptorBytes =>
        Status == FileOperationRecoverySecurityDescriptorStatus.SameQueriedDescriptorBytes;
}

public static class FileOperationRecoverySecurityDescriptorComparer
{
    public static FileOperationRecoverySecurityDescriptorComparison Compare(
        FileSecurityDescriptorEvidence? recorded,
        FileSecurityDescriptorEvidence? current)
    {
        if (recorded is null)
        {
            return new FileOperationRecoverySecurityDescriptorComparison(
                FileOperationRecoverySecurityDescriptorStatus.NoRecordedEvidence,
                Recorded: null,
                Current: current,
                SecurityInformationMaskMatches: null,
                Sha256Matches: null);
        }

        if (current is null)
        {
            return new FileOperationRecoverySecurityDescriptorComparison(
                FileOperationRecoverySecurityDescriptorStatus.Unavailable,
                recorded,
                Current: null,
                SecurityInformationMaskMatches: null,
                Sha256Matches: null);
        }

        var maskMatches = recorded.SecurityInformation == current.SecurityInformation;
        var digestMatches = string.Equals(
            recorded.Sha256HexDigest,
            current.Sha256HexDigest,
            StringComparison.Ordinal);
        var same = maskMatches && digestMatches;

        return new FileOperationRecoverySecurityDescriptorComparison(
            same
                ? FileOperationRecoverySecurityDescriptorStatus.SameQueriedDescriptorBytes
                : FileOperationRecoverySecurityDescriptorStatus.DifferentQueriedDescriptorBytes,
            recorded,
            current,
            maskMatches,
            digestMatches);
    }
}
