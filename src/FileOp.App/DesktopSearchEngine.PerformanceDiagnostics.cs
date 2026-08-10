using System.Diagnostics;
using FileOp.Core.Performance;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    public async ValueTask<PerformanceDiagnosticsSnapshot> CapturePerformanceDiagnosticsAsync()
    {
        ThrowIfDisposed();
        var capturedAt = DateTimeOffset.UtcNow;
        var state = State;
        var root = StorageRootPath;
        var probes = new List<PerformanceProbeMeasurement>(3);

        var timerOverhead = MeasureTimerOverheadMicroseconds();
        probes.Add(new PerformanceProbeMeasurement(
            "Timer baseline",
            "local process",
            timerOverhead,
            "Minimum Stopwatch start/stop cost across 128 samples; shown to bound measurement overhead."));

        if (!state.IsBusy &&
            (state.Mode is DesktopSearchMode.Native or DesktopSearchMode.Fallback))
        {
            var searchStart = Stopwatch.GetTimestamp();
            var search = await SearchAsync(string.Empty, limit: 1).ConfigureAwait(false);
            probes.Add(new PerformanceProbeMeasurement(
                "Indexed search probe",
                state.Mode == DesktopSearchMode.Native ? "native index" : "profile fallback",
                ToMicroseconds(Stopwatch.GetElapsedTime(searchStart)),
                $"Exact probe: empty query, limit 1; returned {search.Count:N0} item(s)."));

            if (root is not null)
            {
                var storageStart = Stopwatch.GetTimestamp();
                var analysis = await AnalyzeStorageAsync(root, maxEntries: 1).ConfigureAwait(false);
                probes.Add(new PerformanceProbeMeasurement(
                    "Storage root probe",
                    state.Mode == DesktopSearchMode.Native ? "native index" : "profile fallback",
                    ToMicroseconds(Stopwatch.GetElapsedTime(storageStart)),
                    $"Exact probe: root analysis, maxEntries 1; {analysis.UniqueFileCount:N0} unique file(s) in scope."));
            }
        }

        var (totalBytes, freeBytes) = ReadVolumeCapacity(root);
        return new PerformanceDiagnosticsSnapshot(
            capturedAt,
            state.Mode.ToString(),
            state.Status,
            state.IndexedItemCount,
            root,
            totalBytes,
            freeBytes,
            probes);
    }

    private static long MeasureTimerOverheadMicroseconds()
    {
        long minimumTicks = long.MaxValue;
        for (var sample = 0; sample < 128; sample++)
        {
            var start = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetTimestamp() - start;
            minimumTicks = Math.Min(minimumTicks, elapsed);
        }

        return Math.Max(0, checked((long)Math.Ceiling(minimumTicks * 1_000_000d / Stopwatch.Frequency)));
    }

    private static long ToMicroseconds(TimeSpan elapsed) =>
        Math.Max(0, checked((long)Math.Ceiling(elapsed.TotalMicroseconds)));

    private static (long? TotalBytes, long? FreeBytes) ReadVolumeCapacity(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return (null, null);
        }

        try
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(rootPath));
            if (string.IsNullOrWhiteSpace(volumeRoot))
            {
                return (null, null);
            }

            var drive = new DriveInfo(volumeRoot);
            if (!drive.IsReady)
            {
                return (null, null);
            }

            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
