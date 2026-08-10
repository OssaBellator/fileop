# Storage optimization advisor

## Purpose

FileOp's Storage surface is intended to help improve storage use and system performance, not merely browse files quickly. The first optimization slice is deliberately read-only: it turns the existing native metadata index into evidence-backed review candidates without scanning file contents or silently deleting anything.

The initial advisor exposes three views of an indexed directory or volume:

- **Largest files** — files at or above the configured large-file threshold, ranked by physical allocation when known and otherwise by logical size.
- **Old large files** — the large-file set filtered by an explicit last-write age threshold. Age is a review signal only; it is not evidence that a file is disposable.
- **Same-size candidate groups** — distinct physical files with the same logical length. This is only a cheap duplicate prefilter for later content verification.

## Default policy

The current policy is explicit and serialized with the analysis result:

- large-file minimum: 512 MiB;
- same-size candidate minimum: 64 MiB;
- stale age: 180 days;
- largest files returned: 50;
- old large files returned: 50;
- same-size groups returned: 25;
- sample files per same-size group: 8.

These values are policy choices, not a hidden health score. Later UI work can make them user-configurable without changing the underlying evidence model.

## Shared-index semantics

Native analysis reads the same per-volume SQLite namespace used by Search, Files and Storage. It requires a valid durable NTFS checkpoint and a cross-process read lease. It does not perform a second recursive filesystem scan.

Hard-link aliases are collapsed before physical-file candidate selection. A stable `(volume serial, file reference)` identifies one physical file; when identity is unavailable, the indexed path is treated conservatively as a distinct candidate.

Largest-file ranking uses `allocated_length` when the native index knows it and falls back to logical length only when physical allocation is unavailable.

## Same-size candidates are not duplicates

Equal length is not content equality. A same-size group means only that two or more distinct physical indexed files have the same logical byte length.

The displayed potential savings is therefore a **logical upper bound**:

`logical bytes per file × (candidate file count - 1)`

FileOp must not present those bytes as guaranteed reclaimable space. A later duplicate-confirmation stage should hash or otherwise verify contents through a separately reviewed, bounded, lazy workflow before any group can be promoted from candidate to confirmed duplicate evidence.

## Native-only first slice

The initial Optimize reclaim view is available only while the native NTFS index is active. The fallback crawler is intentionally not used for reclaim recommendations yet because a profile-scoped snapshot must not be presented as a complete volume optimization analysis.

Fallback parity can be added later only with clear scope labeling and equivalent hard-link/accounting semantics.

## Safety boundary

This feature is analysis only. It does not:

- delete, move, replace, truncate or overwrite files;
- execute queued Files operations;
- hash file contents;
- clear caches or temporary directories;
- modify registry settings;
- disable services or startup applications;
- change power plans, memory settings or filesystem policy;
- run SQLite `VACUUM` or force WAL checkpoints;
- calculate an opaque health/performance score.

Any future cleanup action remains behind explicit user selection, reviewed preflight, recovery evidence and fresh authorization at the mutation boundary.

## Performance direction

Optimize now includes measured performance evidence alongside reclaim candidates:

- exact bounded Search/root-Storage latency probes;
- timer-overhead disclosure;
- current volume free-space capacity;
- helper-owned main/WAL/SHM index footprint;
- SQLite page/freelist evidence;
- the default `cache_size` target observed on the read-only diagnostics connection, explicitly not resident RAM.

Useful next layers include:

1. lazy content verification for same-size duplicate candidates;
2. user-controlled cleanup review for known disposable locations, with provenance and rollback where possible;
3. explicit USN/checkpoint freshness and catch-up backlog;
4. low-frequency latency distributions with strict foreground/overhead budgets;
5. startup/background-work analysis based on observable resource impact rather than registry folklore;
6. storage pressure recommendations tied to actual free-space and growth-history evidence.

The product should avoid registry cleaners, RAM boosters, broad service disabling and similarly ungrounded optimization claims.

## Validation without GitHub Actions

`tools/verify_storage_optimization.py` exercises the ranking, age filtering, hard-link collapse and same-size upper-bound model with randomized inventories and guards the read-only source/UI wiring. `tools/verify_performance_diagnostics.py` and `tools/verify_index_diagnostics.py` separately guard measured-performance and helper-index evidence. They are included in `tools/test-local.ps1 -OfflineOnly`.

The Windows/.NET local gate additionally contains focused SQLite and named-pipe protocol regressions. Those native tests require the Windows/.NET toolchain and are not replaced by the Python model verifiers.
