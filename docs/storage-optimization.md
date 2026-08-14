# Storage optimization advisor

## Purpose

FileOp's Storage surface is intended to improve storage understanding and system performance without turning metadata heuristics into automatic cleanup. Optimize consumes the shared filesystem index and presents progressively stronger evidence while keeping mutation authority separate.

The native advisor includes:

- **Largest files** — files at or above the native large-file threshold, ranked by physical allocation when known and otherwise by logical size.
- **Old large files** — the large-file set filtered by an explicit last-write age threshold. Age is a review signal only.
- **Same-size candidate groups** — distinct physical indexed files with equal logical length.
- **Explicit duplicate verification** — bounded full-file SHA-256 over sampled paths under the desktop user's token.
- **Current physical reclaim evidence** — post-hash identity/link/allocation revalidation from the already-open file handles.
- **Persisted view thresholds** — user-controlled same-or-stricter filters stored as policy-relative multipliers.
- **Known-location review** — conservative Downloads/current-user-Temp provenance rules over the same native indexed metadata.
- **Measured Performance evidence** — bounded foreground probes and explicit diagnostics/capture providers, not generic “PC optimization.”

## Default native policy

The current helper policy is explicit and serialized with every `StorageOptimizationAnalysis`:

- large-file minimum: 512 MiB;
- same-size candidate minimum: 64 MiB;
- stale age: 180 days;
- largest files returned: 50;
- old large files returned: 50;
- same-size groups returned: 25;
- sample files per same-size group: 8.

These values are policy choices, not a hidden score.

The threshold panel can make the displayed large/same-size/age thresholds **stricter** without rerunning the helper. It rejects looser values because a client-side filter cannot reconstruct evidence omitted by the bounded native query.

## Shared-index semantics

Native analysis reads the same per-volume SQLite namespace used by Search, Files and Storage. It requires a valid durable NTFS checkpoint and a shared cross-process read lease. It does not perform a second recursive filesystem scan.

Hard-link aliases are collapsed before physical-file candidate selection. A stable `(volume serial, file reference)` identity represents one physical indexed file when available; missing identity falls back conservatively.

Largest-file ranking uses `allocated_length` when the native index knows it and falls back to logical length only when physical allocation is unavailable.

## Duplicate evidence hierarchy

### 1. Same-size candidate

Equal length is not content equality. Same-size groups are only a cheap prefilter.

The full-group potential logical savings value is:

`logical bytes per file × (candidate file count - 1)`

That is a candidate upper bound, not verified duplicate or guaranteed reclaimable space.

### 2. SHA-256-matched sampled paths

The user must explicitly request verification for one current same-size group. FileOp fully hashes only whole sampled files that fit the hard verification budget. Content is read by the unelevated desktop under the user's token rather than through the indexing helper.

The default policy permits at most 8 sampled files and 2 GiB of whole-file content. If fewer than two whole files fit, verification refuses before opening candidate paths. Partial-file hashes never become duplicate evidence.

A SHA-256 match establishes content equality only for the fully hashed sampled paths. Raw digest values are not persisted or exposed as cleanup authority.

### 3. Current physical reclaim upper bound

For SHA-256 matching sampled paths, FileOp reuses the still-open read handles to revalidate current physical identity, hard-link count, logical length and allocated bytes.

Hard-link aliases collapse again. Only current singleton-link physical files can contribute to the deletion-based reclaim upper bound, and at least one content-equivalent physical file remains in the accounting model.

The resulting physical number is still a **current upper bound**, not deletion authorization. It becomes stale after verification handles close and files later change.

See `docs/same-size-content-verification.md` and `docs/physical-reclaim-evidence.md`.

## Persisted threshold semantics

Optimize presets are derived from the native policy returned with the analysis. Supported multipliers are deliberately same-or-stricter than the helper baseline.

Changing a preset:

- filters in-memory evidence only;
- does not rerun the helper;
- does not hash files;
- does not start ETW;
- does not scan the filesystem;
- does not change result caps.

The selected supported multipliers can be persisted under the current user's LocalApplicationData `FileOp/preferences` directory. FileOp stores semantic policy-relative multipliers rather than raw byte/day thresholds. Each fresh analysis resolves those multipliers again against the helper's current `StorageOptimizationPolicy`, then passes the result through the same-or-stricter completeness check.

Malformed, unsupported or unavailable preference data falls back to the baseline and must not make Optimize unavailable.

Original same-size group indices are retained so current content/physical verification evidence stays attached when a stricter threshold hides a group and later re-shows it.

This persistence is **not helper-policy transport**. FileOp still cannot ask protocol v8 to collect files below the helper's native baseline. A future below-baseline feature would need a separately reviewed typed analysis-policy request; the current UI fails closed instead of presenting incomplete evidence as complete.

See `docs/storage-threshold-overlay.md` and `docs/storage-threshold-preferences.md`.

## Known-location review

The provenance-based review uses the native stale-large analysis for the current user's Downloads and Temp roots. Same-volume locations use the active primary native source; redirected locations may use another already indexed, checkpointed native volume without switching the primary Search/Storage source or changing protocol v8.

Downloads uses explicit package/archive/disk-image extension allow-lists after a file is already in the bounded native stale-large source set. `.exe` is deliberately not inferred to be an installer. User Temp uses location provenance without extension guessing.

Every row exposes a rule ID/reason, and the UI states that location, age, size and extension do **not** establish safe deletion. Aggregate bytes are candidate measured bytes, not guaranteed reclaim.

Cross-volume evidence is accepted only after the selected indexed source is bound to a unique current volume identity/root, has a usable checkpoint, reaches bounded catch-up currentness, and still matches after analysis. A redirected location with no matching indexed source remains outside the active volume; FileOp does not silently scan or rebuild it.

Candidate rows have a separate defense-in-depth provenance boundary before classification: the indexed path must be fully qualified, normalize within the reviewed root using separator-bound Windows path semantics, and agree with the row's indexed name/extension metadata. Malformed, relative, sibling-prefix, other-volume, or metadata-inconsistent rows are filtered without changing the upstream stale-candidate count/truncation disclosure.

**Check readiness** is a separate read-only current-path check and can revalidate a cross-volume candidate against its exact owning indexed source. **Review in Files** remains primary-volume-only and has no direct-filesystem fallback for cross-volume candidates. Neither action becomes delete consent.

See `docs/known-location-review.md`, `docs/known-location-files-handoff.md` and `docs/cleanup-readiness.md`.

## Native-only Optimize/reclaim boundary

Optimize reclaim evidence is available only while the native NTFS index is active. The fallback crawler is useful for Search/browse/general Storage, but its ordinary records do not preserve the same NTFS file-identity and allocated-length evidence required for equivalent hard-link-deduplicated physical reclaim accounting.

FileOp therefore does **not** claim crawler-fallback Optimize parity today. Adding such a path would require equivalent identity/allocation semantics or a deliberately weaker, separately labelled evidence model; it must not silently reuse native reclaim wording.

## Mutation boundary

Optimize itself remains non-destructive. It does not:

- delete, move, replace, truncate or overwrite files;
- issue a `FileDeleteOperationUserAuthorizationReceipt`;
- invoke `FileDeleteOperationOrchestrator`;
- execute queued Copy/Move plans;
- automatically hash content;
- clear caches or temporary directories;
- label known-location candidates “junk”;
- expose a `SafeToDelete` flag;
- modify registry settings;
- disable services or startup applications;
- change power plans, memory settings or filesystem policy;
- run SQLite `VACUUM` or force WAL checkpoints;
- calculate an opaque cleanup/health/performance score.

FileOp now has a separately reviewed **Files file-delete session**. That does not make Optimize evidence reusable authorization. If a user later chooses permanent deletion, the file must be selected in Files and independently pass the Files recovery scan, read-only delete preflight, canonical/protected-location validation, explicit confirmation, authorization, durable history begin and reviewed mutation orchestration.

Cleanup readiness and physical reclaim values remain evidence only and cannot skip that boundary.

## Performance evidence

Optimize/Performance surfaces include measured evidence such as:

- exact bounded Search/root-Storage latency probes;
- low-frequency latency distributions with timer-overhead disclosure;
- current volume free-space capacity and growth-history context;
- helper-owned main/WAL/SHM index footprint;
- SQLite page/freelist evidence;
- durable checkpoint/USN freshness evidence;
- FileOp process footprint;
- explicit bounded Disk I/O attribution and timing/loss evidence;
- system/device/health/fragmentation evidence where the reviewed provider supports it.

Performance and full Optimize refreshes are coordinated around explicit Disk I/O capture and content verification so FileOp does not intentionally contaminate the measurement it is trying to attribute.

The product avoids registry cleaners, RAM boosters, broad service disabling and similarly ungrounded optimization claims.

## Validation without GitHub Actions

`tools/test-local.ps1` is the authoritative validation inventory. Relevant offline guards include the native Optimize model, explicit content-verification model, physical reclaim accounting, threshold overlay/preferences, known-location review and Performance/index/Disk-I/O providers.

Run the portable aggregate gate:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1 -OfflineOnly
```

The complete Windows gate additionally runs the .NET builds, regression/integration tests, WinUI x64 build, bundled-helper checks and real helper-process handshake:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

The portable models do not replace native Windows/.NET validation; they provide a reproducible no-Actions layer around the same reviewed boundaries.
