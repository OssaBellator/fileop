using System.Diagnostics;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Performance;
using FileOp.Windows.IndexingService;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    public async ValueTask<PerformanceDiagnosticsSnapshot> CapturePerformanceDiagnosticsAsync()
    {
        ThrowIfDisposed();
        var capturedAt = DateTimeOffset.UtcNow;
        var state = State;
        var root = StorageRootPath;
        var probes = new List<PerformanceProbeMeasurement>(4);

        var timerOverhead = MeasureTimerOverheadMicroseconds();
        probes.Add(new PerformanceProbeMeasurement(
            "Timer baseline",
            "local process",
            timerOverhead,
            "Minimum Stopwatch start/stop cost across 128 samples; shown to bound measurement overhead.",
            PerformanceProbeKind.TimerBaseline));

        if (!state.IsBusy &&
            (state.Mode is DesktopSearchMode.Native or DesktopSearchMode.Fallback))
        {
            var searchStart = Stopwatch.GetTimestamp();
            var search = await SearchAsync(string.Empty, limit: 1).ConfigureAwait(false);
            probes.Add(new PerformanceProbeMeasurement(
                "Indexed search probe",
                state.Mode == DesktopSearchMode.Native ? "native index" : "profile fallback",
                ToMicroseconds(Stopwatch.GetElapsedTime(searchStart)),
                $"Exact probe: empty query, limit 1; returned {search.Count:N0} item(s).",
                PerformanceProbeKind.Search));

            if (root is not null)
            {
                var storageStart = Stopwatch.GetTimestamp();
                var analysis = await AnalyzeStorageAsync(root, maxEntries: 1).ConfigureAwait(false);
                probes.Add(new PerformanceProbeMeasurement(
                    "Storage root probe",
                    state.Mode == DesktopSearchMode.Native ? "native index" : "profile fallback",
                    ToMicroseconds(Stopwatch.GetElapsedTime(storageStart)),
                    $"Exact probe: root analysis, maxEntries 1; {analysis.UniqueFileCount:N0} unique file(s) in scope.",
                    PerformanceProbeKind.Storage));
            }
        }

        IndexDatabaseDiagnostics? indexDatabase = null;
        string? indexDatabaseStatus = null;
        if (!state.IsBusy && state.Mode == DesktopSearchMode.Native)
        {
            var indexStart = Stopwatch.GetTimestamp();
            try
            {
                indexDatabase = await CaptureNativeIndexDatabaseDiagnosticsAsync().ConfigureAwait(false);
                if (indexDatabase is not null)
                {
                    probes.Add(new PerformanceProbeMeasurement(
                        "Index database probe",
                        "native helper index",
                        ToMicroseconds(Stopwatch.GetElapsedTime(indexStart)),
                        "Exact probe: helper-owned read-only database/WAL/SHM file metadata plus SQLite page_size, page_count, freelist_count, cache_size and journal_mode."));
                }
                else
                {
                    indexDatabaseStatus = "Native index diagnostics became unavailable while the source was changing.";
                }
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code is
                    IndexingServiceErrorCode.Busy or
                    IndexingServiceErrorCode.SnapshotRequired or
                    IndexingServiceErrorCode.VolumeNotFound)
            {
                indexDatabaseStatus = exception.Error.Message;
            }
        }
        else if (state.Mode == DesktopSearchMode.Fallback)
        {
            indexDatabaseStatus = "Helper-owned index database metrics are native-only; fallback mode has no persistent NTFS database to report.";
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
            probes,
            indexDatabase,
            indexDatabaseStatus);
    }

    private async ValueTask<IndexDatabaseDiagnostics?> CaptureNativeIndexDatabaseDiagnosticsAsync()
    {
        await _searchOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            if (_nativeSession is not { Client.IsConnected: true } || _primaryVolume is null)
            {
                return null;
            }

            await _nativeOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            try
            {
                if (_nativeSession is not { Client.IsConnected: true } session ||
                    _primaryVolume is not { } volume)
                {
                    return null;
                }

                var response = await session.Client.GetIndexDiagnosticsAsync(
                    new IndexingIndexDiagnosticsRequest(volume.VolumeIdentity, volume.RootPath),
                    _lifetimeCancellation.Token).ConfigureAwait(false);
                return response.Diagnostics;
            }
            finally
            {
                _nativeOperationGate.Release();
            }
        }
        finally
        {
            _searchOperationGate.Release();
        }
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
