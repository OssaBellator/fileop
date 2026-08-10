# Performance diagnostics

## Purpose

FileOp's performance optimisation starts with measurement. The product should show what FileOp can observe, the scope and units of that observation, and the exact probe used before recommending any tuning action.

The diagnostics surface lives inside Storage **Optimize** because storage pressure, index/query performance and reclaim evidence are closely related. It remains a separate evidence panel rather than being folded into a synthetic health score.

## Current snapshot

A diagnostics refresh records:

- capture timestamp;
- current FileOp source mode (`Native`, `Fallback` or other engine state);
- current index status text;
- indexed item count;
- current Storage root when one is available;
- total and available-free bytes for the containing ready volume;
- a bounded end-to-end Search probe;
- a bounded end-to-end Storage root probe;
- a local `Stopwatch` baseline that makes timing overhead visible;
- when the native helper/index is valid, helper-owned SQLite file/page/cache evidence;
- the durable native checkpoint and, when accessible, one live USN-journal metadata observation;
- a bounded in-memory session distribution for the exact Search/Storage probes.

The UI displays raw units directly. It does not turn them into a green/yellow/red health grade.

## Exact foreground probes

Diagnostics are **on demand**. Entering or refreshing Optimize runs at most these public application probes when the current source is ready:

1. `SearchAsync(string.Empty, limit: 1)`;
2. `AnalyzeStorageAsync(root, maxEntries: 1)`.

The recorded detail string states those exact limits and the observed result count/scope. Measurements are end-to-end from the desktop coordinator's point of view, so native results include the same gate/IPC/service path a foreground user request experiences.

The timer baseline is the minimum `Stopwatch` start/stop interval across 128 local samples. It is not subtracted from the Search/Storage timings; it is exposed only to bound measurement overhead.

The producer assigns typed `PerformanceProbeKind` values to these known probes. The session-history layer also recognizes the historical exact probe names so older in-process snapshots remain compatible.

## Helper-owned index database evidence

Protocol v8 uses `GetIndexDiagnostics` for the current native volume/root. The desktop never passes a database filename. The helper resolves the same deterministic database identity used by native indexing, Browse, History and Optimize.

The operation requires:

1. the requested physical NTFS volume/root to still be attached;
2. the shared cross-process read lease;
3. a valid durable checkpoint; and
4. a read-only/query-only SQLite connection.

The response captures:

- main `.sqlite` file bytes;
- `-wal` file bytes when present;
- `-shm` file bytes when present;
- SQLite `page_size`;
- SQLite `page_count`;
- SQLite `freelist_count`;
- SQLite `cache_size` observed on the diagnostics reader connection;
- SQLite `journal_mode`;
- persisted indexed-item count.

The UI derives total helper-file footprint and reusable freelist bytes from these values. These are observations, not automatic recommendations.

### Freelist semantics

`freelist_count × page_size` estimates pages inside the main SQLite database that SQLite can reuse for later writes. FileOp calls these **reusable pages**.

This number is not equivalent to immediately reclaimable disk space. The database file does not shrink merely because pages are on the freelist, and FileOp does not infer that running `VACUUM` is desirable. Diagnostics never run `VACUUM`.

### Cache semantics

FileOp currently does not set `PRAGMA cache_size` on its index connections. The **reader-connection cache default** shown by diagnostics is therefore the default target observed on the read-only diagnostics connection, not a FileOp-selected tuning value.

FileOp follows SQLite's sign convention when formatting that observation:

- positive values are page counts and are converted with the current page size;
- negative values are interpreted as approximate KiB targets.

The reader-connection cache default is not observed resident memory, committed memory, live cache occupancy, a cache-hit ratio, or evidence that RAM should be freed. FileOp does not trim working sets in response to it.

### WAL semantics

The WAL file size is displayed as part of the helper's current file footprint. A large WAL is not by itself evidence that a forced checkpoint would improve performance. Diagnostics do not call `wal_checkpoint` or otherwise force a checkpoint.

### Provider isolation

Index-database evidence is native-only. Fallback mode explicitly reports that it has no persistent NTFS helper database to describe.

A Busy, SnapshotRequired or detached-volume response from the index-diagnostics provider is isolated from other already-valid evidence. Search latency, Storage latency and free-space capacity can still be displayed when the SQLite detail provider is temporarily unavailable.

## USN/checkpoint freshness

The same protocol-v8 index-diagnostics response can include optional durable/live journal evidence. The helper reads the durable checkpoint through the same read-only SQLite connection and then performs at most one `NtfsUsnJournal.Query(volume)` metadata observation.

FileOp exposes:

- checkpoint age from its durable timestamp;
- durable and live journal identity continuity;
- whether the durable `NextUsn` lies inside the current readable journal window;
- USN backlog distance (`live NextUsn - durable NextUsn`) only when continuity is valid;
- retention headroom (`durable NextUsn - LowestValidUsn`) only when continuity is valid.

These values are **USN sequence-position differences**. They are not file/event counts, byte counts, elapsed time, synchronization-duration estimates or health grades.

The UI distinguishes changed journal identity, checkpoint below the retention floor, checkpoint ahead of the live head and a readable continuous window. Invalid continuity never produces a made-up backlog value.

Live-journal access failure is isolated: the already-valid durable checkpoint and SQLite evidence remain visible. Older protocol-v8 helpers may omit the optional freshness fields.

Diagnostics do not read journal records, advance/delete a checkpoint, trigger synchronization or rebuild the index.

See `docs/usn-freshness.md` for the detailed continuity contract.

## Session latency distributions

The current measurement stays visible separately from a bounded session history. Each explicit diagnostics refresh contributes at most one Search sample and one Storage sample.

For the one active source/root window FileOp retains the most recent **20 samples per probe**. With the current two probes, at most **40 elapsed-time samples** are held. A native/fallback mode change or root/volume change clears the old window rather than mixing unlike sources or retaining unbounded per-root history.

Duplicate rows for one exact probe in a single snapshot count once. Timer-baseline and other probes never enter the Search/Storage distribution.

The UI shows sample count/capacity, minimum, integer-midpoint median, maximum and nearest-rank p95 only after five explicit samples. The p95 is a small-sample descriptive statistic, not an SLA, health grade, benchmark or prediction of future latency.

The history is process-memory only. It is not persisted and has no timer, scheduler, background sampler or telemetry path.

See `docs/latency-distributions.md` for the statistical contract.

## Foreground and polling policy

There is no continuous performance poller. Diagnostics do not use a periodic timer, filesystem watcher or background scan.

The bounded probes call the existing Search and Storage APIs and therefore obey the same foreground coordination and native-service rules as normal user work. The helper-owned database/journal probe also goes through the desktop foreground/native gates. A refresh is user-visible work, not hidden maintenance.

A performance-probe failure is isolated from Storage reclaim analysis. FileOp may still show largest/old/same-size storage evidence when the diagnostics panel cannot complete.

## Volume pressure semantics

Capacity is read from the current Storage root's containing `DriveInfo` when Windows reports a ready drive:

- `TotalSize` is total volume capacity;
- `AvailableFreeSpace` is available free bytes;
- used bytes are `max(0, total - free)`;
- free percentage is clamped to `[0, 100]` to remain conservative across a raced capacity read.

Unknown/unready capacity remains unknown rather than becoming zero.

## Explicit non-goals

The current diagnostics surface does **not**:

- clean the registry;
- free/trim another process's working set;
- disable services or startup entries;
- change power plans;
- run defrag/TRIM;
- run SQLite `VACUUM` or force a WAL checkpoint;
- modify SQLite cache settings;
- modify page-file or memory settings;
- launch recursive filesystem scans;
- continuously poll CPU/disk/memory counters;
- produce an opaque performance or health score.

Those actions are not implied by latency, capacity, journal or index-database measurements.

## Next evidence layers

Useful follow-ups should remain separately reviewable:

1. Storage history + current free-space pressure correlation, without treating correlation as causation or projecting a disk-full date from sparse observations;
2. Windows storage-I/O attribution with identified owners;
3. startup/background activity diagnostics based on measured resource impact;
4. SMART/media health and TRIM/defrag applicability where Windows exposes reliable evidence;
5. CPU/memory pressure only when FileOp can identify an actionable owner/workload and measure its own observation overhead.

Administrative tuning remains outside `FileOp.Indexer` unless a separately reviewed privilege boundary earns it.

## Validation without GitHub Actions

`tools/test-local.ps1 -OfflineOnly` includes the performance, latency-distribution, index-database and USN/checkpoint verifiers.

- `tools/verify_performance_diagnostics.py` guards the exact bounded foreground probes, volume-capacity semantics and no-fake-optimiser boundary.
- `tools/verify_latency_distributions.py` models bounded retention, median/p95 semantics and active-source isolation and requires the Core/UI/test integration to be present.
- `tools/verify_index_diagnostics.py` models footprint/page/freelist/cache arithmetic and guards read-only SQLite access.
- `tools/verify_usn_freshness.py` models journal continuity and requires checkpoint loading, one live metadata query, UI state handling and additive protocol-v8 transport coverage.

The Windows/.NET gate adds the focused Core/SQLite/named-pipe regression tests when a suitable Windows toolchain is available. Native/WinUI execution is not implied by the portable verifier results.
