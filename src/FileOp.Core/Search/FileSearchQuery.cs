using System.Globalization;

namespace FileOp.Core.Search;

public sealed record FileSearchQuery(
    string Raw,
    IReadOnlyList<string> Terms,
    IReadOnlySet<string> Extensions,
    long? MinimumSize,
    long? MaximumSize,
    int Limit)
{
    public long? ExactSize { get; init; }

    public static FileSearchQuery Parse(string? raw, int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        raw ??= string.Empty;
        var terms = new List<string>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long? minimumSize = null;
        long? maximumSize = null;
        long? exactSize = null;

        foreach (var token in Tokenize(raw))
        {
            if (token.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
            {
                var extension = token[4..].Trim().TrimStart('.');
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    extensions.Add(extension);
                }

                continue;
            }

            if (token.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            {
                ParseSizeFilter(token[5..], ref minimumSize, ref maximumSize, ref exactSize);
                continue;
            }

            terms.Add(token);
        }

        return new FileSearchQuery(raw, terms, extensions, minimumSize, maximumSize, limit)
        {
            ExactSize = exactSize,
        };
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        var current = new List<char>();
        var quoted = false;

        foreach (var character in value)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Count > 0)
                {
                    yield return new string([.. current]);
                    current.Clear();
                }

                continue;
            }

            current.Add(character);
        }

        if (current.Count > 0)
        {
            yield return new string([.. current]);
        }
    }

    private static void ParseSizeFilter(
        string value,
        ref long? minimumSize,
        ref long? maximumSize,
        ref long? exactSize)
    {
        var comparison = value.StartsWith('>') ? '>' : value.StartsWith('<') ? '<' : '=';
        var number = comparison == '=' ? value : value[1..];

        if (!TryParseByteSize(number, out var bytes))
        {
            return;
        }

        switch (comparison)
        {
            case '>':
                minimumSize = bytes;
                break;
            case '<':
                maximumSize = bytes;
                break;
            default:
                exactSize = bytes;
                break;
        }
    }

    private static bool TryParseByteSize(string value, out long bytes)
    {
        bytes = 0;
        var normalized = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var multiplier = 1L;
        var suffixes = new (string Suffix, long Multiplier)[]
        {
            ("tb", 1024L * 1024 * 1024 * 1024),
            ("gb", 1024L * 1024 * 1024),
            ("mb", 1024L * 1024),
            ("kb", 1024L),
            ("b", 1L),
        };

        foreach (var suffix in suffixes)
        {
            if (!normalized.EndsWith(suffix.Suffix, StringComparison.Ordinal))
            {
                continue;
            }

            normalized = normalized[..^suffix.Suffix.Length];
            multiplier = suffix.Multiplier;
            break;
        }

        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) ||
            !double.IsFinite(numeric) ||
            numeric < 0 ||
            numeric > long.MaxValue / (double)multiplier)
        {
            return false;
        }

        bytes = checked((long)(numeric * multiplier));
        return true;
    }
}
