using System;

namespace FileOp.Core.Operations;

public enum FileContentFingerprintAlgorithm
{
    Sha256 = 1,
}

/// <summary>
/// Immutable logical-file content evidence. A matching fingerprint is evidence only;
/// it does not grant delete, replace, recovery, or Undo authority.
/// </summary>
public sealed record FileContentFingerprint
{
    public const int Sha256HexLength = 64;

    public FileContentFingerprint(
        FileContentFingerprintAlgorithm algorithm,
        string hexDigest)
    {
        if (algorithm != FileContentFingerprintAlgorithm.Sha256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(algorithm),
                algorithm,
                "Only SHA-256 content fingerprints are currently supported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(hexDigest);
        if (hexDigest.Length != Sha256HexLength)
        {
            throw new ArgumentException(
                $"A SHA-256 content fingerprint must contain exactly {Sha256HexLength} hexadecimal characters.",
                nameof(hexDigest));
        }

        foreach (var character in hexDigest)
        {
            if (!Uri.IsHexDigit(character))
            {
                throw new ArgumentException(
                    "A content fingerprint digest may contain hexadecimal characters only.",
                    nameof(hexDigest));
            }
        }

        Algorithm = algorithm;
        HexDigest = hexDigest.ToLowerInvariant();
    }

    public FileContentFingerprintAlgorithm Algorithm { get; }

    public string HexDigest { get; }
}
