# Storage optimization advisor

## Purpose

FileOp's Storage surface is intended to help improve storage use and system performance, not merely browse files quickly. Optimize consumes the shared filesystem index and presents evidence at progressively stronger levels without silently deleting anything or inventing an opaque health score.

The native advisor includes:

- **Largest files** — files at or above the native large-file threshold, ranked by physical allocation when known and otherwise by logical size.
- **Old large files** — the large-file set filtered by an explicit last-write age threshold. Age is a review signal only.
- **Same-size candidate groups** — distinct physical indexed files with equal logical length.
- **Explicit duplicate verification** — bounded full-file SHA-256 over sampled paths under the desktop user's token.
- **Current physical reclaim evidence** — post-hash identity/link/allocation revalidation from the already-open file handles.
- **Session view thresholds** — user-controlled stricter filters that never imply evidence below the native helper baseline.
- **Known-location review** — conservative Downloads/current-user-Temp review rules over the same native indexed metadata.
- **Measured Performance evidence** — bounded foreground probes and explicit Disk I/O attribution, not generic “PC optimization.”

## Default native policy

The current helper policy is explicit and serialized with each `StorageOptimizationAnalysis`:

- large-file minimum: 512 MiB;
- same-size candidate minimum: 64 MiB;
- stale age: 180 days;
- largest files returned: 50;
- old large files returned: 50;
- same-size groups returned: 25;
- sample files per same-size group: 8.

These values are policy choices, not a hidden score.

The session threshold panel can make the displayed large/same-size/age thresholds **stricter** without rerunning the helper. It rejects looser values because a client-side filter cannot reconstruct evidence omitted by the bounded native query.

Persisting threshold preferences or transporting lower thresholds into the helper remains a separate typed-policy decision.

## Shared-index semantics

Native analysis reads the same per-volume SQLite namespace used by Search, Files and Storage. It requires a valid durable NTFS checkpoint and a cross-process read lease. It does not perform a second recursive filesystem scan.

Hard-link aliases are collapsed before physical-file candidate selection. A `(volume serial, file reference)` identity identifies one physical indexed file when available; missing identities fall back conservatively to path identity.

Largest-file ranking uses `allocated_length` when the native index knows it and falls back to logical length only when physical allocation is unavailable.

## Duplicate evidence hierarchy

### Same-size candidate

Equal length is not content equality. Same-size groups are only a cheap prefilter.

The full-group potential logical savings value remains:

`logical bytes per file × (candidate file count - 1)`

That is a candidate upper bound, not guaranteed reclaimable space.

### SHA-256-matched sampled paths

The user must explicitly request verification for one same-size group. FileOp then fully hashes only whole sampled files that fit the hard verification budget. Content is read by the desktop under the user's token rather than through the elevated indexer.

A hash match proves content equality only for the fully hashed sampled paths. Digest values are not persisted or presented as cleanup authority.

### Current physical reclaim upper bound

For SHA-256 matching sampled paths, FileOp reuses the still-open read handles to revalidate current physical identity, hard-link count, logical length and allocation.

Hard-link aliases collapse again. Only current singleton-link physical files can contribute to the deletion-based reclaim upper bound. At least one content-equivalent physical file remains in the accounting model.

The resulting physical number is still a **current upper bound**, not deletion authorization. It becomes stale after verification handles close and files later change.

## Session threshold semantics

Optimize's view-threshold panel derives its presets from the native policy returned with the analysis.

Changing a preset:

- filters in-memory evidence only;
- does not rerun the helper;
- does not hash files;
- does not start ETW;
- does not scan the filesystem;
- does not change result caps.

Original same-size group indices are retained so existing content/physical verification evidence remains attached when a group is hidden and later re-shown.

## Known-location review

The first provenance-based review slice uses the native stale-large analysis for the current user's Downloads and Temp roots when those roots are on the active indexed volume.

Downloads uses explicit package/archive extension allow-lists. `.exe` is deliberately not inferred to be an installer. User Temp uses location provenance without extension guessing.

Every row exposes a rule ID and reason, and the UI states that location, age, size and extension do **not** establish safe deletion. Aggregate bytes are candidate measured bytes, not guaranteed reclaim.

If a known location lies on another volume, FileOp reports that scope explicitly instead of silently scanning it. If the native stale-large source reaches its cap, the review is marked potentially incomplete.

See `docs/known-location-review.md` for the exact rules and boundary.

## Native-only reclaim boundary

Optimize reclaim evidence is available only while the native NTFS index is active. The fallback crawler is intentionally not used for reclaim recommendations because a profile-scoped fallback snapshot must not be presented as a complete volume optimization model.

Fallback parity can be added later only with honest scope labeling and equivalent hard-link/accounting semantics.

## Safety boundary

Optimize analysis does not:

- delete, move, replace, truncate or overwrite files;
- execute queued Files operations;
- automatically hash content;
- clear caches or temporary directories;
- label known-location candidates “junk”;
- expose a `SafeToDelete` flag;
- modify registry settings;
- disable services or startup applications;
- change power plans, memory settings or filesystem policy;
- run SQLite `VACUUM` or force WAL checkpoints;
- calculate an opaque cleanup/health/performance score.

Any future destructive operation remains behind explicit user selection, the reviewed Files preflight/recovery/authorization boundary, and fresh action-time revalidation.

## Performance evidence

Optimize includes measured performance evidence alongside storage review:

- exact bounded Search/root-Storage latency probes;
- low-frequency latency distributions with timer-overhead disclosure;
- current volume free-space capacity and growth-history context;
- helper-owned main/WAL/SHM index footprint;
- SQLite page/freelist evidence;
- durable checkpoint/USN freshness evidence;
- FileOp's own process footprint;
- explicit bounded Disk I/O attribution capture.

Performance and full Optimize refreshes are kept out of an active Disk I/O capture so FileOp does not inject avoidable index work into the measurement it is trying to attribute.

The product should avoid registry cleaners, RAM boosters, broad service disabling and similarly ungrounded optimization claims.

## Validation without GitHub Actions

The offline gate now layers dedicated model/source guards rather than weakening the original Optimize verifier:

- `tools/verify_storage_optimization.py` — native ranking, age, hard-link collapse and same-size candidate semantics;
- `tools/verify_same_size_content_verification.py` — bounded explicit full-file hashing;
- `tools/verify_physical_reclaim_evidence.py` — current physical identity/link/allocation accounting;
- `tools/verify_storage_threshold_overlay.py` — stricter session filters and lifecycle coordination;
- `tools/verify_known_location_review.py` — provenance/rule classification and review-only safety boundary;
- Performance/index/Disk-I/O verifiers for the measured diagnostics surface.

They are wired into `tools/test-local.ps1 -OfflineOnly`.

The Windows/.NET local gate additionally contains focused native regressions. Those tests require the Windows/.NET toolchain and are not replaced by the Python models; native execution is not claimed from the current sandbox.
