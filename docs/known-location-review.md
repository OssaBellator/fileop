# Known-location storage review

## Purpose

FileOp helps users find storage worth reviewing without turning folder names, age or file extensions into fake deletion authority. Known-location review remains **read-only and deliberately conservative** for:

- current-user Downloads;
- the current-user Temp path.

The review itself never authorizes cleanup. Permanent deletion, if later chosen in Files, still goes through the separately reviewed Files preflight/recovery/authorization boundary.

## Evidence source and scope

The desktop resolves each current-user known-location path and asks the existing native index for the exact subtree. It does not recursively enumerate Downloads or Temp in the desktop process and does not introduce another filesystem crawler.

Protocol remains v8. The existing protocol already binds storage optimization analysis to `VolumeIdentity`, `VolumeRootPath`, and `DirectoryPath`, so cross-volume review requires no new Indexer operation.

### Current baseline

Known-location review consumes the existing Optimize **stale-large** source set. With the current default policy, a source file must already be:

- at least 512 MiB by physical allocation when known, otherwise logical length; and
- at least 180 days old by last-write metadata.

Those thresholds are intentionally conservative. This slice does not pretend that smaller or younger files have been examined.

If the upstream stale-large list reaches `MaxStaleLargeFiles`, the location is marked as potentially truncated. The UI then explicitly says additional matching files may exist.

## Downloads provenance

Downloads is resolved as the current user's Windows known folder rather than guessed as `%USERPROFILE%\\Downloads`. This matters because Windows known folders can be redirected, including to another volume.

The Downloads rules are extension allow-lists applied **only after** a file is already in the native stale-large source set. They do not widen the candidate set.

### Rule `downloads.old-package-extension.v1`

Recognized package formats: `.msi`, `.msix`, `.msixbundle`, `.appx`, `.appxbundle`, `.msu`.

### Rule `downloads.old-archive-extension.v1`

Recognized archive formats: `.zip`, `.7z`, `.rar`, `.tar`, `.gz`, `.tgz`, `.bz2`, `.xz`.

### Rule `downloads.old-disk-image-extension.v1`

Recognized disk-image format: `.iso`.

`.iso` remains within the existing review candidate set; this refinement only gives it disk-image-specific provenance instead of the older combined archive/disk-image reason.

`.img`, `.vhd`, and `.vhdx` are not added. `.exe` is intentionally **not** classified as an installer. Package, archive or disk-image extension evidence does not establish safe deletion.

## User Temp provenance

The Temp root comes from `Path.GetTempPath()` under the desktop user's environment.

Rule `user-temp.old-large-file.v1` surfaces stale-large indexed files underneath that exact current-user Temp root. It does not infer type from extension. Being in Temp is still only review evidence.

The SQLite Optimize analyzer canonicalizes indexed directory scope by removing a trailing directory separator from non-volume-root paths. Cross-volume review therefore compares the returned analysis root and requested known-location path with equivalent normalized Windows path semantics; a formatting-only `D:\Temp\` versus `D:\Temp` difference is not treated as source drift.

## Cross-volume indexed-source boundary

Same-volume known locations continue to use the active primary native Optimize source.

When a known location is outside the active primary volume, FileOp now:

1. derives the location's filesystem root;
2. reads the already connected helper's indexed-volume descriptors;
3. requires exactly one descriptor for the normalized active-primary root and exactly one descriptor for the normalized secondary root; an absent secondary remains outside the active volume, while duplicate root matches are treated as an ambiguous catalog and fail closed rather than using the first descriptor;
4. verifies that the service-reported primary descriptor identity/root still matches the selected primary native source;
5. distinguishes a genuine durable-checkpoint absence (`State == SnapshotRequired`) from a temporarily unavailable descriptor; helper busy descriptors intentionally report `HasCheckpoint == false` while maintenance owns the index, so other no-checkpoint states are treated as transient rather than as evidence that a rebuild is required;
6. requires the selected secondary volume to have an available existing checkpoint;
7. performs bounded journal catch-up with the existing `SyncVolume` / `CatchUpAsync` path;
8. requires catch-up to reach `IsCurrent == true`;
9. submits `AnalyzeStorageOptimization` with that exact secondary `VolumeIdentity`, `VolumeRootPath`, and known-location path;
10. verifies the returned analysis root is the same normalized Windows path as the requested known location;
11. re-reads indexed-volume descriptors after analysis, requiring unique primary/secondary root matches, the same identities/roots and a still-available secondary checkpoint;
12. verifies that the selected primary source still has the same identity/root before accepting the secondary evidence.

A redirected location with no matching indexed volume remains `OutsideActiveVolume`. A duplicate/ambiguous root match, genuine missing checkpoint, required rebuild/elevation, bounded catch-up that remains behind, transient busy/maintenance descriptor, lost checkpoint, or source identity/root drift marks that location unavailable. FileOp does **not** silently rebuild a secondary volume and does not scan the path directly.

The primary Search/Storage volume is never replaced merely to review a redirected known location.

After both known locations have been evaluated, the producer performs one final service-catalog freshness check for the active primary source even if both locations were same-volume. The snapshot persists the active primary `VolumeIdentity` as well as its root. Publication is refused unless the selected in-process primary and a unique current `GetVolumes` descriptor still agree on that identity/root. This closes the interval where a different physical volume could appear under the same drive letter before background sync reports the transition.

The current native volume catalog is drive-letter-rooted (`DriveInfo.GetDrives()` with drive-letter root filtering). The cross-volume verifier pins that contract because the current `Path.GetPathRoot` selector depends on it. If native discovery later exposes directory-mounted volumes, selection must move to descriptor-containment/longest-root semantics rather than silently assuming the host drive root.

## Candidate actions

**Check readiness** remains available for cross-volume candidates. It performs separate current canonical/path/identity checks directly against the candidate and its known-location root and remains non-authorizing. Before the direct path read and again before rendering the result, the coordinator also requires the cached snapshot's primary identity/root to match both the selected native source and a unique current service-catalog descriptor.

**Review in Files** remains available only for candidates on the active Files indexed volume. Files is still primary-volume-bound, so a cross-volume candidate's button is disabled and the coordinator independently refuses programmatic handoff. Before navigation and again before applying an asynchronous selection hint, the handoff revalidates the snapshot's primary identity/root against the current service catalog. There is no direct-filesystem browsing fallback.

Storage review/readiness evidence never becomes reusable delete consent. If permanent deletion is later chosen, the user must select/review the file in Files and pass the Files delete session's current recovery checks, canonical identity/protected-location validation, explicit confirmation, durable history begin, and reviewed mutation orchestration.

## Candidate bytes

Each candidate keeps logical bytes, allocated bytes when known, last-write time, provenance, rule ID and reason. The UI displays allocated bytes when available and otherwise logical bytes. Aggregate bytes are labelled **candidate measured bytes**; they are not guaranteed reclaimable space or deletion authorization. Totals use saturating arithmetic.

## Failure isolation

Known-location review has its own status inside Optimize. Failure of one location does not erase successful base Optimize or Performance evidence.

Source changes invalidate cached review. Transient native-index busy/freshness failures fail the affected review closed rather than substituting crawler evidence. A temporary descriptor state is not reported as a missing durable checkpoint merely because the helper deliberately suppresses checkpoint claims while maintenance owns the index.

Full Optimize/Performance refreshes remain blocked during explicit Disk I/O attribution capture so known-location queries are not added as avoidable FileOp workload during measurement.

## Windows Downloads path resolution

`WindowsKnownFolderPathResolver` uses `SHGetKnownFolderPath` with Downloads known-folder ID `{374DE290-123F-4565-9164-39C4925E467B}` and current-user token semantics. It balances COM initialization, frees the returned path buffer, does not enumerate the folder, and does not read the registry.

## Deliberate non-goals

This slice does not:

- delete, move or quarantine candidates from Storage;
- label anything junk or expose `SafeToDelete`;
- classify `.exe` as an installer;
- add `.img`, `.vhd` or `.vhdx` candidates;
- inspect package/archive/disk-image contents;
- scan known folders outside the shared native index;
- automatically rebuild a secondary volume;
- switch the primary Search/Storage source for review;
- add cross-volume Files browsing;
- lower or persist Optimize thresholds;
- change protocol v8;
- create timers/background polling;
- calculate a cleanup/health score.

## Validation without GitHub Actions

`tools/verify_known_location_review.py` continues to pin provenance/candidate membership, measured-byte semantics and the original review-only safety boundary.

`tools/verify_cross_volume_known_location_review.py` models unique indexed-volume selection, ambiguous duplicate-root refusal, genuine `SnapshotRequired` versus temporary no-checkpoint descriptors, bounded catch-up currentness, normalized returned-root equivalence, post-capture identity/checkpoint validation, primary-source preservation and the final native-catalog freshness check used for same-volume publication and cached actions. Its source guards pin exact volume-bound requests, before/after descriptor reads, reusable source-freshness gate ordering, the helper busy-descriptor/snapshot-required contract, drive-letter-root discovery, matching SQLite/request path normalization, no secondary rebuild/crawler fallback, protocol v8, cross-volume Files-handoff refusal and continued read-only readiness.

`tools/verify_known_location_files_handoff.py` continues to model the primary-volume Files containment rule. `tools/verify_cleanup_readiness.py` pins that readiness can operate on a cross-volume review candidate without becoming mutation authority. The cross-volume verifier additionally requires both action coordinators to call the native catalog freshness boundary before and after their asynchronous/current-path work.

All are part of `tools/test-local.ps1`; the complete Windows/.NET/WinUI local gate remains mandatory before merge and can be batched after the portable/source-review work.
