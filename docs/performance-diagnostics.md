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
- when the native helper/index is valid, helper-owned SQLite file/page/cache evidence.

The UI displays raw units directly. It does not turn them into a green/yellow/red health grade.

## Exact foreground probes

Diagnostics are **on demand**. Entering or refreshing Optimize runs at most these public application probes when the current source is ready:

1. `SearchAsync(string.Empty, limit: 1)`;
2. `AnalyzeStorageAsync(root, maxEntries: 1)`.

The recorded detail string states those exact limits and the observed result count/scope. Measurements are end-to-end from the desktop coordinator's point of view, so native results include the same gate/IPC/service path a foreground user request experiences.

The timer baseline is the minimum `Stopwatch` start/stop interval across 128 local samples. It is not subtracted from the Search/Storage timings; it is exposed only to bound measurement overhead.

## Helper-owned index database evidence

Protocol v8 adds `GetIndexDiagnostics` for the current native volume/root. The desktop never passes a database filename. The helper resolves the same deterministic database identity used by native indexing, Browse, History and Optimize.

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
- SQLite `cache_size` setting;
- SQLite `journal_mode`;
- persisted indexed-item count.

The UI derives total helper-file footprint and reusable freelist bytes from these values. These are observations, not automatic recommendations.

### Freelist semantics

`freelist_count × page_size` estimates pages inside the main SQLite database that SQLite can reuse for later writes. FileOp calls these **reusable pages**.

This number is not equivalent to immediately reclaimable disk space. The database file does not shrink merely because pages are on the freelist, and FileOp does not infer that running `VACUUM` is desirable. Diagnostics never run `VACUUM`.

### Cache semantics

SQLite `PRAGMA cache_size` is a configuration target. FileOp follows SQLite's sign convention:

- positive values are page counts and are converted with the current page size;
- negative values are interpreted as approximate KiB targets.

The resulting **cache target** is not observed resident memory, committed memory, a cache-hit ratio, or evidence that RAM should be freed. FileOp does not trim working sets in response to it.

### WAL semantics

The WAL file size is displayed as part of the helper's current file footprint. A large WAL is not by itself evidence that a forced checkpoint would improve performance. Diagnostics do not call `wal_checkpoint` or otherwise force a checkpoint.

### Provider isolation

Index-database evidence is native-only. Fallback mode explicitly reports that it has no persistent NTFS helper database to describe.

A Busy, SnapshotRequired or detached-volume response from the index-diagnostics provider is isolated from other already-valid evidence. Search latency, Storage latency and free-space capacity can still be displayed when the SQLite detail provider is temporarily unavailable.

## Foreground and polling policy

There is no continuous performance poller. Diagnostics do not use a periodic timer, filesystem watcher or background scan.

The bounded probes call the existing Search and Storage APIs and therefore obey the same foreground coordination and native-service rules as normal user work. The helper-owned database probe also goes through the desktop foreground/native gates. A refresh is user-visible work, not hidden maintenance.

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

Those actions are not implied by latency, capacity or index-database measurements.

## Next evidence layers

Useful follow-ups should remain separately reviewable:

1. explicit USN/checkpoint freshness and catch-up backlog;
2. latency distributions rather than one on-demand sample, with a strict low-frequency/foreground budget;
3. Storage history + current free-space pressure correlation;
4. Windows storage-I/O attribution with identified owners;
5. startup/background activity diagnostics based on measured resource impact;
6. SMART/media health and TRIM/defrag applicability where Windows exposes reliable evidence;
7. CPU/memory pressure only when FileOp can identify an actionable owner/workload and measure its own observation overhead.

Administrative tuning remains outside `FileOp.Indexer` unless a separately reviewed privilege boundary earns it.

## Validation without GitHub Actions

`tools/verify_performance_diagnostics.py` performs randomized capacity arithmetic checks and repository guards that require:

- exact bounded Search/Storage probe arguments;
- helper-owned index diagnostics integration;
- timer-overhead disclosure;
- no periodic timer/watcher;
- no registry/service/working-set/defrag tuning APIs;
- protocol-v8 stability while preserving earlier operations;
- Optimize integration and independent diagnostics failure handling;
- explicit user-facing no-fake-optimiser wording.

`tools/verify_index_diagnostics.py` separately performs randomized footprint/page/freelist/cache arithmetic, a standard-library SQLite WAL fixture and repository guards requiring read-only/query-only access, shared database identity, protocol-v8 request/response wiring, explicit cache/freelist wording and absence of `VACUUM`, forced WAL checkpointing or mutation APIs.

Both verifiers are included in `tools/test-local.ps1 -OfflineOnly`. `PerformanceDiagnosticsTests`, `IndexDatabaseDiagnosticsTests` and `IndexingIndexDiagnosticsProtocolTests` add .NET/native regression coverage when the Windows local gate is available.
