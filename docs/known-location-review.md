# Known-location storage review

## Purpose

FileOp should help users find storage worth reviewing without turning folder names, age or file extensions into fake deletion authority.

The first known-location slice is therefore **read-only and deliberately conservative**. It reuses the native Storage Optimize index evidence for two current-user locations:

- Downloads;
- the current-user Temp path.

No cleanup action is added.

## Evidence source and scope

The desktop resolves the current-user location path, then asks the existing native `AnalyzeStorageOptimizationAsync` path for that exact indexed subtree.

The helper therefore continues to use the shared read-only SQLite namespace, durable checkpoint and cross-process read lease. This feature does not recursively enumerate Downloads or Temp in the desktop process and does not introduce another filesystem crawler.

Protocol remains v8 in this first slice.

### Current baseline

Known-location review consumes the existing Optimize **stale-large** source set. With today's default policy, that means a source file must already be:

- at least 512 MiB by physical allocation when known, otherwise logical length; and
- at least 180 days old by last-write metadata.

Those thresholds are intentionally conservative. This slice does not pretend that smaller or younger files have been examined.

If the upstream stale-large list reaches `MaxStaleLargeFiles`, the location is marked as potentially truncated. The UI then explicitly says additional matching files may exist.

## Downloads provenance

Downloads is resolved as the current user's Windows known folder rather than guessed as `%USERPROFILE%\\Downloads`. This matters because Windows known folders can be redirected.

The first Downloads rules are extension allow-lists applied **only after** a file is already in the native stale-large source set.

### Rule `downloads.old-package-extension.v1`

Recognized package formats:

- `.msi`
- `.msix`
- `.msixbundle`
- `.appx`
- `.appxbundle`
- `.msu`

### Rule `downloads.old-archive-extension.v1`

Recognized archive/disk-image formats:

- `.zip`
- `.7z`
- `.rar`
- `.iso`
- `.tar`
- `.gz`
- `.tgz`
- `.bz2`
- `.xz`

`.exe` is intentionally **not** classified as an installer. An executable name or location is not sufficient provenance to infer what it is or whether it is disposable.

Unrecognized Downloads extensions are ignored by this first rule set.

## User Temp provenance

The Temp root comes from `Path.GetTempPath()` under the desktop user's environment.

Rule `user-temp.old-large-file.v1` surfaces stale-large indexed files underneath that exact current-user Temp root. It does not infer type from extension.

Being in Temp is still only review evidence. Files can remain in use, be required by an application, or become relevant again after the index snapshot. This slice provides no delete button and no `SafeToDelete` flag.

## Active-volume boundary

This first slice reviews a known location only when it lies on FileOp's **currently active native indexed volume**.

A redirected Downloads or Temp path on another volume is shown as `OutsideActiveVolume`. FileOp does not silently scan the other volume and does not hide the scope limitation.

Cross-volume review can be added later by explicitly selecting the matching indexed volume and preserving equivalent checkpoint/accounting semantics.

## Candidate bytes

Each candidate keeps:

- logical bytes;
- allocated bytes when the index knows them;
- last-write time;
- provenance;
- rule ID and rule reason.

The UI displays allocated bytes when available and otherwise logical bytes. Aggregate bytes are labelled **candidate measured bytes**. They are not guaranteed reclaimable space and are not deletion authorization.

Totals use saturating arithmetic.

## Failure isolation

Known-location review has its own status inside Optimize.

If Downloads cannot be resolved, Temp cannot be resolved, a location is outside the active volume, or a location cannot be queried from the current native index, that location receives an explicit status/detail. Other valid Optimize and Performance evidence remains visible.

A catastrophic known-location producer failure is also isolated by the MainWindow coordinator and does not erase successful base reclaim analysis or Performance diagnostics.

Source changes invalidate the cached review. Transient native-index busy states that clear Optimize mark the source for a real reload when readiness returns.

## Disk-I/O measurement coordination

Full Optimize refresh and Performance refresh are blocked while the explicit Disk I/O attribution capture is active. The parent Refresh button is disabled during the trace.

This prevents the known-location index queries—and the existing Optimize/Performance foreground queries—from being introduced as avoidable FileOp workload during an attribution capture.

## Windows Downloads path resolution

`WindowsKnownFolderPathResolver` uses `SHGetKnownFolderPath` with the Downloads known-folder ID `{374DE290-123F-4565-9164-39C4925E467B}` and the current-user token semantics.

It balances COM initialization when it owns initialization, tolerates an already-initialized different apartment, and frees the returned path buffer with `Marshal.FreeCoTaskMem`.

The resolver returns a path only; it does not enumerate the folder and does not read the registry.

## Deliberate non-goals

This slice does not:

- delete, move or quarantine candidates;
- label anything junk;
- expose a `SafeToDelete` or equivalent boolean;
- classify `.exe` as an installer;
- inspect signatures, MSI metadata or executable version resources;
- scan known folders outside the shared native index;
- lower the native Optimize thresholds;
- aggregate across volumes;
- change protocol v8;
- create timers/background polling;
- calculate a cleanup/health score.

A future destructive workflow still needs explicit user selection plus the existing Files preflight/recovery/authorization boundary and fresh action-time revalidation.

## Validation without GitHub Actions

`tools/verify_known_location_review.py` models extension/provenance classification, `.exe` exclusion, measured-byte semantics and saturated totals. Its source guards pin:

- explicit rule IDs and allow-lists;
- absence of `SafeToDelete` and delete APIs;
- the exact Downloads known-folder resolver boundary;
- use of the existing `AnalyzeStorageOptimizationAsync` path rather than crawling;
- active-volume scope disclosure;
- no action buttons in the review UI;
- failure isolation and Disk I/O refresh coordination;
- unchanged protocol v8;
- inclusion in `tools/test-local.ps1 -OfflineOnly`.

Focused .NET tests cover Core classification, `.exe` exclusion, Temp provenance, cap disclosure and saturation. Native Windows/.NET/WinUI execution is not claimed from the current sandbox.
