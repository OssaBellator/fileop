namespace FileOp.Core.Storage;

public interface IStorageHistoryStore
{
    ValueTask<long> SaveSnapshotAsync(
        StorageFileTypeAnalysis analysis,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<StorageHistorySnapshot>> GetSnapshotsAsync(
        string rootPath,
        int limit = 90,
        CancellationToken cancellationToken = default);

    ValueTask<int> PruneBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default);
}

public sealed record StorageHistoryCategorySnapshot(
    StorageFileCategory Category,
    long LogicalBytes,
    long? AllocatedBytes,
    int FileCount,
    int HardLinkAliasCount,
    int TypeCount)
{
    public int UniqueFileCount => FileCount - HardLinkAliasCount;
}

public sealed record StorageHistorySnapshot(
    long Id,
    string RootPath,
    DateTimeOffset CapturedAt,
    long LogicalBytes,
    long? AllocatedBytes,
    int FileCount,
    int HardLinkAliasCount,
    int TypeCount,
    IReadOnlyList<StorageHistoryCategorySnapshot> Categories)
{
    public int UniqueFileCount => FileCount - HardLinkAliasCount;
}

public sealed record StorageHistoryCategoryDelta(
    StorageFileCategory Category,
    long LogicalBytesDelta,
    long? AllocatedBytesDelta,
    long FileCountDelta,
    long HardLinkAliasCountDelta,
    long TypeCountDelta);

public sealed record StorageHistoryDelta(
    DateTimeOffset From,
    DateTimeOffset To,
    long LogicalBytesDelta,
    long? AllocatedBytesDelta,
    long FileCountDelta,
    long HardLinkAliasCountDelta,
    long TypeCountDelta,
    IReadOnlyList<StorageHistoryCategoryDelta> Categories)
{
    public static StorageHistoryDelta Between(
        StorageHistorySnapshot older,
        StorageHistorySnapshot newer)
    {
        ArgumentNullException.ThrowIfNull(older);
        ArgumentNullException.ThrowIfNull(newer);

        if (!string.Equals(
                NormalizeRoot(older.RootPath),
                NormalizeRoot(newer.RootPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Storage history snapshots must describe the same root.", nameof(newer));
        }

        if (newer.CapturedAt < older.CapturedAt)
        {
            throw new ArgumentException("The newer storage history snapshot cannot precede the older snapshot.", nameof(newer));
        }

        var olderCategories = older.Categories.ToDictionary(static item => item.Category);
        var newerCategories = newer.Categories.ToDictionary(static item => item.Category);
        var categories = Enum.GetValues<StorageFileCategory>()
            .Select(category => CreateCategoryDelta(
                category,
                olderCategories.GetValueOrDefault(category),
                newerCategories.GetValueOrDefault(category)))
            .Where(static delta =>
                delta.LogicalBytesDelta != 0 ||
                (delta.AllocatedBytesDelta is { } allocatedDelta && allocatedDelta != 0) ||
                delta.FileCountDelta != 0 ||
                delta.HardLinkAliasCountDelta != 0 ||
                delta.TypeCountDelta != 0)
            .OrderByDescending(static delta => Magnitude(delta.AllocatedBytesDelta ?? delta.LogicalBytesDelta))
            .ThenBy(static delta => delta.Category)
            .ToArray();

        return new StorageHistoryDelta(
            older.CapturedAt,
            newer.CapturedAt,
            newer.LogicalBytes - older.LogicalBytes,
            older.AllocatedBytes is { } olderAllocated && newer.AllocatedBytes is { } newerAllocated
                ? newerAllocated - olderAllocated
                : null,
            (long)newer.FileCount - older.FileCount,
            (long)newer.HardLinkAliasCount - older.HardLinkAliasCount,
            (long)newer.TypeCount - older.TypeCount,
            categories);
    }

    private static StorageHistoryCategoryDelta CreateCategoryDelta(
        StorageFileCategory category,
        StorageHistoryCategorySnapshot? older,
        StorageHistoryCategorySnapshot? newer)
    {
        var olderAllocated = GetAllocatedOrZero(older);
        var newerAllocated = GetAllocatedOrZero(newer);
        long? allocatedDelta = olderAllocated.HasValue && newerAllocated.HasValue
            ? newerAllocated.Value - olderAllocated.Value
            : null;

        return new StorageHistoryCategoryDelta(
            category,
            (newer?.LogicalBytes ?? 0) - (older?.LogicalBytes ?? 0),
            allocatedDelta,
            (long)(newer?.FileCount ?? 0) - (older?.FileCount ?? 0),
            (long)(newer?.HardLinkAliasCount ?? 0) - (older?.HardLinkAliasCount ?? 0),
            (long)(newer?.TypeCount ?? 0) - (older?.TypeCount ?? 0));
    }

    private static long? GetAllocatedOrZero(StorageHistoryCategorySnapshot? snapshot) =>
        snapshot is null ? 0L : snapshot.AllocatedBytes;

    private static ulong Magnitude(long value) =>
        value >= 0 ? (ulong)value : (ulong)(-(value + 1)) + 1;

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (trimmed.Length == 3 &&
            char.IsLetter(trimmed[0]) &&
            trimmed[1] == ':' &&
            (trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:\\";
        }

        return trimmed.TrimEnd('\\', '/');
    }
}
