namespace FileOp.Core.Storage;

public sealed record StorageHistoryPressureEvidence(
    DateTimeOffset? OlderCapturedAt,
    DateTimeOffset? NewerCapturedAt,
    long? LatestStorageBytes,
    bool LatestStorageUsesPhysicalAllocation,
    long? StorageDeltaBytes,
    bool DeltaUsesPhysicalAllocation,
    TimeSpan? ObservationInterval,
    long? VolumeTotalBytes,
    long? VolumeFreeBytes)
{
    public double? VolumeFreePercent =>
        VolumeTotalBytes is { } total && total > 0 && VolumeFreeBytes is { } free
            ? Math.Clamp(free * 100d / total, 0d, 100d)
            : null;

    public double? FreeSpaceToLastPositivePhysicalGrowthMultiple
    {
        get
        {
            if (!DeltaUsesPhysicalAllocation ||
                StorageDeltaBytes is not { } growth ||
                growth <= 0 ||
                VolumeFreeBytes is not { } free)
            {
                return null;
            }

            return free / (double)growth;
        }
    }

    public static StorageHistoryPressureEvidence Analyze(
        IReadOnlyList<StorageHistorySnapshot> snapshots,
        long? volumeTotalBytes,
        long? volumeFreeBytes)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var total = volumeTotalBytes is > 0 ? volumeTotalBytes : null;
        long? free = volumeFreeBytes is { } rawFree
            ? Math.Max(0, rawFree)
            : null;
        if (total is { } knownTotal && free is { } knownFree)
        {
            free = Math.Min(knownFree, knownTotal);
        }

        if (snapshots.Count == 0)
        {
            return new StorageHistoryPressureEvidence(
                null,
                null,
                null,
                false,
                null,
                false,
                null,
                total,
                free);
        }

        var chronological = snapshots
            .OrderBy(static snapshot => snapshot.CapturedAt)
            .ToArray();
        var newest = chronological[^1];
        var latestUsesPhysical = newest.AllocatedBytes.HasValue;
        var latestBytes = newest.AllocatedBytes ?? newest.LogicalBytes;

        if (chronological.Length < 2)
        {
            return new StorageHistoryPressureEvidence(
                null,
                newest.CapturedAt,
                latestBytes,
                latestUsesPhysical,
                null,
                false,
                null,
                total,
                free);
        }

        var older = chronological[^2];
        var delta = StorageHistoryDelta.Between(older, newest);
        var deltaUsesPhysical = delta.AllocatedBytesDelta.HasValue;
        var deltaBytes = deltaUsesPhysical
            ? delta.AllocatedBytesDelta
            : delta.LogicalBytesDelta;

        return new StorageHistoryPressureEvidence(
            older.CapturedAt,
            newest.CapturedAt,
            latestBytes,
            latestUsesPhysical,
            deltaBytes,
            deltaUsesPhysical,
            newest.CapturedAt - older.CapturedAt,
            total,
            free);
    }
}
