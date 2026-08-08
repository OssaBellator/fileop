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

        if (!string.Equals(older.RootPath, newer.RootPath, StringComparison.OrdinalIgnoreCase))
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
                delta.AllocatedBytesDelta is null ||
                delta.AllocatedBytesDelta != 0 ||
                delta.FileCountDelta != 0 ||
                delta.HardLinkAliasCountDelta != 0 ||
                delta.TypeCountDelta != 0)
            .OrderByDescending(static delta => Math.Abs(delta.AllocatedBytesDelta ?? delta.LogicalBytesDelta))
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
        var olderLogical = older?.LogicalBytes ?? 0;
        var newerLogical = newer?.LogicalBytes ?? 0;
        long? allocatedDelta;
        if (older is null && newer is null)
        {
            allocatedDelta = 0;
        }
        else if ((older?.AllocatedBytes ?? (older is null ? 0 : null)) is { } olderAllocated &&
                 (newer?.AllocatedBytes ?? (newer is null ? 0 : null)) is { } newerAllocated)
        {
            allocatedDelta = newerAllocated - olderAllocated;
        }
        else
        {
            allocatedDelta = null;
        }

        return new StorageHistoryCategoryDelta(
            category,
            newerLogical - olderLogical,
            allocatedDelta,
            (long)(newer?.FileCount ?? 0) - (older?.FileCount ?? 0),
            (long)(newer?.HardLinkAliasCount ?? 0) - (older?.HardLinkAliasCount ?? 0),
            (long)(newer?.TypeCount ?? 0) - (older?.TypeCount ?? 0));
    }
}
