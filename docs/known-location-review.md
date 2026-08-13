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

## Cross-volume indexed-source boundary

Same-volume known locations continue to use the active primary native Optimize source.

When a known location is outside the active primary volume, FileOp now:

1. derives the location's filesystem root;
2. reads the already connected helper's indexed-volume descriptors;
3. selects the exact descriptor by normalized volume root;
4. requires that secondary volume to already have a checkpoint;
5. performs bounded journal catch-up with the existing `SyncVolume` / `CatchUpAsync` path;
6. requires catch-up to reach `IsCurrent == true`;
7. submits `AnalyzeStorageOptimization` with that exact secondary `VolumeIdentity`, `VolumeRootPath`, and known-location path;
8. re-reads indexed-volume descriptors after analysis and requires the same volume identity/root/checkpoint before accepting evidence;
9. verifies that the active primary source did not change while the secondary evidence was captured.

A redirected location with no matching indexed volume remains `OutsideActiveVolume`. A matching secondary volume with no checkpoint, a required rebuild/elevation, bounded catch-up that remains behind, or source identity/checkpoint drift is marked unavailable. FileOp does **not** silently rebuild a secondary volume and does not scan the path directly.

The primary Search/Storage volume is never replaced merely to review a redirected known location.

## Candidate actions

**Check readiness** remains available for cross-volume candidates. It performs separate current canonical/path/identity checks directly against the candidate and its known-location root and remains non-authorizing.

**Review in Files** remains available only for candidates on the active Files indexed volume. Files is still primary-volume-bound, so a cross-volume candidate's button is disabled and the coordinator independently refuses programmatic handoff. There is no direct-filesystem browsing fallback.

Storage review/readiness evidence never becomes reusable delete consent. If permanent deletion is later chosen, the user must select/review the file in Files and pass the Files delete session's current recovery checks, canonical identity/protected-location validation, explicit confirmation, durable history begin, and reviewed mutation orchestration.

## Candidate bytes

Each candidate keeps logical bytes, allocated bytes when known, last-write time, provenance, rule ID and reason. The UI displays allocated bytes when available and otherwise logical bytes. Aggregate bytes are labelled **candidate measured bytes**; they are not guaranteed reclaimable space or deletion authorization. Totals use saturating arithmetic.

## Failure isolation

Known-location review has its own status inside Optimize. Failure of one location does not erase successful base Optimize or Performance evidence.

Source changes invalidate cached review. Transient native-index busy/freshness failures fail the affected review closed rather than substituting crawler evidence.

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

`tools/verify_cross_volume_known_location_review.py` models indexed-volume selection, checkpoint/currentness refusal, post-capture identity validation and primary-source preservation. Its source guards pin exact volume-bound requests, two descriptor reads, no secondary rebuild/crawler fallback, protocol v8, cross-volume Files-handoff refusal, and continued read-only readiness.

`tools/verify_known_location_files_handoff.py` continues to model the primary-volume Files containment rule. `tools/verify_cleanup_readiness.py` pins that readiness can operate on a cross-volume review candidate without becoming mutation authority.

All are part of `tools/test-local.ps1`; the complete Windows/.NET/WinUI local gate remains mandatory before merge.
