namespace FileOp.Core.Performance;

public sealed record PerformanceProbeMeasurement(
    string Name,
    string Scope,
    long ElapsedMicroseconds,
    string Detail);

public sealed record PerformanceDiagnosticsSnapshot(
    DateTimeOffset CapturedAt,
    string SourceMode,
    string IndexState,
    int IndexedItemCount,
    string? RootPath,
    long? VolumeTotalBytes,
    long? VolumeFreeBytes,
    IReadOnlyList<PerformanceProbeMeasurement> Probes)
{
    public long? VolumeUsedBytes =>
        VolumeTotalBytes is { } total && VolumeFreeBytes is { } free
            ? Math.Max(0, total - free)
            : null;

    public double? VolumeFreePercent =>
        VolumeTotalBytes is > 0 and var total && VolumeFreeBytes is { } free
            ? Math.Clamp(free * 100d / total, 0d, 100d)
            : null;
}
