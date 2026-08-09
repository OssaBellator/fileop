using System;

namespace FileOp.Core.Operations;

/// <summary>
/// Versioned SHA-256 evidence over a canonicalized inventory of named $DATA
/// stream names plus their logical sizes. Raw stream names are never persisted.
/// Equality is topology/size evidence only and does not prove stream-content equality.
/// </summary>
public sealed record FileNamedDataStreamTopologyEvidence
{
    public const int CurrentFormatVersion = 1;
    public const int MaximumNamedStreamCount = 4096;

    public FileNamedDataStreamTopologyEvidence(
        int formatVersion,
        int namedStreamCount,
        string sha256HexDigest)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(formatVersion),
                $"Named data-stream topology evidence must use format version {CurrentFormatVersion}.");
        }

        if (namedStreamCount < 0 || namedStreamCount > MaximumNamedStreamCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(namedStreamCount),
                $"Named data-stream topology evidence supports between 0 and {MaximumNamedStreamCount} named streams.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sha256HexDigest);
        if (sha256HexDigest.Length != 64 || !IsHex(sha256HexDigest))
        {
            throw new ArgumentException(
                "Named data-stream topology SHA-256 evidence must contain exactly 64 hexadecimal characters.",
                nameof(sha256HexDigest));
        }

        FormatVersion = formatVersion;
        NamedStreamCount = namedStreamCount;
        Sha256HexDigest = sha256HexDigest.ToLowerInvariant();
    }

    public int FormatVersion { get; }

    public int NamedStreamCount { get; }

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

public enum FileOperationRecoveryNamedDataStreamTopologyStatus
{
    NoRecordedEvidence,
    SameNamesAndSizes,
    DifferentNamesOrSizes,
    Unavailable,
}

public sealed record FileOperationRecoveryNamedDataStreamTopologyComparison(
    FileOperationRecoveryNamedDataStreamTopologyStatus Status,
    FileNamedDataStreamTopologyEvidence? Recorded,
    FileNamedDataStreamTopologyEvidence? Current,
    bool? FormatVersionMatches,
    bool? NamedStreamCountMatches,
    bool? Sha256Matches)
{
    public bool SameNamesAndSizes =>
        Status == FileOperationRecoveryNamedDataStreamTopologyStatus.SameNamesAndSizes;
}

public static class FileOperationRecoveryNamedDataStreamTopologyComparer
{
    public static FileOperationRecoveryNamedDataStreamTopologyComparison Compare(
        FileNamedDataStreamTopologyEvidence? recorded,
        FileNamedDataStreamTopologyEvidence? current)
    {
        if (recorded is null)
        {
            return new FileOperationRecoveryNamedDataStreamTopologyComparison(
                FileOperationRecoveryNamedDataStreamTopologyStatus.NoRecordedEvidence,
                Recorded: null,
                Current: current,
                FormatVersionMatches: null,
                NamedStreamCountMatches: null,
                Sha256Matches: null);
        }

        if (current is null)
        {
            return new FileOperationRecoveryNamedDataStreamTopologyComparison(
                FileOperationRecoveryNamedDataStreamTopologyStatus.Unavailable,
                recorded,
                Current: null,
                FormatVersionMatches: null,
                NamedStreamCountMatches: null,
                Sha256Matches: null);
        }

        var formatMatches = recorded.FormatVersion == current.FormatVersion;
        var countMatches = recorded.NamedStreamCount == current.NamedStreamCount;
        var digestMatches = string.Equals(
            recorded.Sha256HexDigest,
            current.Sha256HexDigest,
            StringComparison.Ordinal);
        var same = formatMatches && countMatches && digestMatches;

        return new FileOperationRecoveryNamedDataStreamTopologyComparison(
            same
                ? FileOperationRecoveryNamedDataStreamTopologyStatus.SameNamesAndSizes
                : FileOperationRecoveryNamedDataStreamTopologyStatus.DifferentNamesOrSizes,
            recorded,
            current,
            formatMatches,
            countMatches,
            digestMatches);
    }
}
