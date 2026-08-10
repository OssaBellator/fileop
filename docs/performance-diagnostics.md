# Performance diagnostics

## Purpose

FileOp's performance optimisation should start with measurement. The product should show what FileOp can observe, the scope and units of that observation, and the exact probe used before recommending any tuning action.

The first diagnostics slice lives inside Storage **Optimize** because storage pressure, index/query performance and reclaim evidence are closely related. It remains a separate evidence panel rather than being folded into a synthetic health score.

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
- a local `Stopwatch` baseline that makes timing overhead visible.

The UI displays free bytes and free percentage directly. It does not turn those values into a green/yellow/red health grade.

## Exact probes

Diagnostics are **on demand**. Entering or refreshing Optimize runs at most these public application probes when the current source is ready:

1. `SearchAsync(string.Empty, limit: 1)`;
2. `AnalyzeStorageAsync(root, maxEntries: 1)`.

The recorded detail string states those exact limits and the observed result count/scope. Measurements are end-to-end from the desktop coordinator's point of view, so native results include the same gate/IPC/service path a foreground user request experiences.

The timer baseline is the minimum `Stopwatch` start/stop interval across 128 local samples. It is not subtracted from the Search/Storage timings; it is exposed only to bound measurement overhead.

## Foreground and polling policy

There is no continuous performance poller. Diagnostics do not use a periodic timer, filesystem watcher or background scan.

The bounded probes call the existing Search and Storage APIs and therefore obey the same foreground coordination and native-service rules as normal user work. A refresh is user-visible work, not hidden maintenance.

A performance-probe failure is isolated from Storage reclaim analysis. FileOp may still show largest/old/same-size storage evidence when the diagnostics panel cannot complete.

## Volume pressure semantics

Capacity is read from the current Storage root's containing `DriveInfo` when Windows reports a ready drive:

- `TotalSize` is total volume capacity;
- `AvailableFreeSpace` is available free bytes;
- used bytes are `max(0, total - free)`;
- free percentage is clamped to `[0, 100]` to remain conservative across a raced capacity read.

Unknown/unready capacity remains unknown rather than becoming zero.

## Explicit non-goals

This slice does **not**:

- clean the registry;
- free/trim another process's working set;
- disable services or startup entries;
- change power plans;
- run defrag/TRIM;
- modify page-file or memory settings;
- launch recursive filesystem scans;
- continuously poll CPU/disk/memory counters;
- produce an opaque performance or health score.

Those actions are not implied by a latency or free-space measurement.

## Next evidence layers

Useful follow-ups should remain separately reviewable:

1. helper-owned index database size and page/cache statistics;
2. explicit USN/checkpoint freshness and catch-up backlog;
3. latency distributions rather than one on-demand sample, with a strict low-frequency/foreground budget;
4. Storage history + current free-space pressure correlation;
5. Windows storage-I/O attribution with identified owners;
6. startup/background activity diagnostics based on measured resource impact;
7. SMART/media health and TRIM/defrag applicability where Windows exposes reliable evidence.

Administrative tuning remains outside `FileOp.Indexer` unless a separately reviewed privilege boundary earns it.

## Validation without GitHub Actions

`tools/verify_performance_diagnostics.py` performs randomized capacity arithmetic checks and repository guards that require:

- exact bounded probe arguments;
- timer-overhead disclosure;
- no periodic timer/watcher;
- no registry/service/working-set/defrag tuning APIs;
- unchanged indexing protocol v7;
- Optimize integration and independent diagnostics failure handling;
- explicit user-facing no-fake-optimiser wording.

The verifier is included in `tools/test-local.ps1 -OfflineOnly`. `PerformanceDiagnosticsTests` adds .NET regression coverage for capacity/unknown semantics when the Windows/.NET local gate is available.
