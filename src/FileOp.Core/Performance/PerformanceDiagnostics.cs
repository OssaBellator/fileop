namespace FileOp.Core.Performance;

public enum PerformanceProbeKind
{
    Other,
    TimerBaseline,
    Search,
    Storage,
}

public sealed record PerformanceProbeMeasurement(
    string Name,
    string Scope,
    long ElapsedMicroseconds,
    string Detail,
    PerformanceProbeKind Kind = PerformanceProbeKind.Other);

public sealed record FileOpProcessResourceSnapshot(
    DateTimeOffset ProcessStartedAt,
    TimeSpan Uptime,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedMemoryBytes,
    int ThreadCount)
{
    public bool HasValidNonNegativeEvidence =>
        Uptime >= TimeSpan.Zero &&
        TotalProcessorTime >= TimeSpan.Zero &&
        WorkingSetBytes >= 0 &&
        PeakWorkingSetBytes >= 0 &&
        PrivateMemoryBytes >= 0 &&
        ManagedMemoryBytes >= 0 &&
        ThreadCount >= 0;
}

public sealed record PerformanceDiagnosticsSnapshot(
    DateTimeOffset CapturedAt,
    string SourceMode,
    string IndexState,
    int IndexedItemCount,
    string? RootPath,
    long? VolumeTotalBytes,
    long? VolumeFreeBytes,
    IReadOnlyList<PerformanceProbeMeasurement> Probes,
    IndexDatabaseDiagnostics? IndexDatabase = null,
    string? IndexDatabaseStatus = null,
    FileOpProcessResourceSnapshot? FileOpResources = null,
    string? FileOpResourcesStatus = null)
{
    public long? VolumeUsedBytes =>
        VolumeTotalBytes is { } total && VolumeFreeBytes is { } free
            ? Math.Max(0, total - free)
            : null;

    public double? VolumeFreePercent =>
        VolumeTotalBytes is { } total && total > 0 && VolumeFreeBytes is { } free
            ? Math.Clamp(free * 100d / total, 0d, 100d)
            : null;
}
