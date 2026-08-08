# Storage growth history

## Purpose

Storage history turns FileOp's current indexed Storage analysis into a compact time series without retaining per-file event history or rescanning the filesystem.

Each observation stores aggregate state only:

- root logical bytes;
- nullable root physical allocation;
- namespace file count;
- hard-link alias count;
- complete extension-group count;
- exact category rollups for the same observation.

The desktop uses those observations to show whole-volume usage over time and the category changes between the newest two observations while keeping persistence proportional to observations and categories rather than files and time.

Folder-level historical contributors remain a later slice. Per-file temporal logging is not part of this model.

## Storage model

`SqliteStorageHistoryStore` writes into the same per-volume SQLite database that contains FileOp's current metadata index. History uses three additive objects:

```text
storage_history_schema_info
storage_history_snapshots
storage_history_categories
```

The history objects have their own schema version. The existing core `schema_info` version continues to describe the current namespace/index schema. This keeps historical-analytics migrations independent without creating a second database.

A snapshot row is unique by ordinal-ignore-case root path plus UTC capture ticks. The store registers `FILEOP_ORDINAL_NOCASE`, backed by `StringComparer.OrdinalIgnoreCase`, rather than SQLite's ASCII-only built-in `NOCASE`. Repeating a write for the same root/timestamp updates the root row and replaces its category rows transactionally.

Category rows are keyed by snapshot plus `StorageFileCategory`. Foreign-key cascade deletion removes category rows whenever retention deletes the owning snapshot.

## Rebuild semantics

The existing `SqliteFileIndex.ClearAsync` clears `files` and durable source checkpoints during a fresh namespace rebuild. It does not delete `storage_history_*` tables.

That is deliberate: a rebuild changes the current representation of the volume but should not erase trustworthy observations captured before the rebuild.

History is scoped by root path inside one per-volume/root database. The native indexing architecture creates a fresh database when the same physical disk moves to a different root because current namespace paths are absolute. Historical observations therefore do not silently cross a drive-letter/root-identity change.

## Protocol v5 service boundary

Protocol v5 exposes two history operations:

```text
CaptureStorageHistory
  VolumeIdentity
  VolumeRootPath
  DirectoryPath

GetStorageHistory
  VolumeIdentity
  VolumeRootPath
  DirectoryPath
  Limit
```

The capture request deliberately contains **no timestamp**. The indexer owns capture time and canonicalizes it to the start of the current UTC hour. Repeated successful captures for the same root during one hour therefore replace the same observation bucket instead of creating timestamp noise.

`GetStorageHistory` defaults to 90 observations and is bounded to the same 4096-row safety ceiling used by the history store. Large framed responses still inherit the existing 8 MiB service cap and `ResponseTooLarge` behavior.

### Capture validity

`StorageHistoryIndexingServiceBackend` wraps the reviewed native backend. Capture first calls native `AnalyzeStorageTypes` with `MaxTypes = 1`.

That call is the trusted semantic read boundary: it resolves the attached volume/root, validates the target path, acquires the process-local operation gate and shared cross-process volume lease, validates the durable NTFS checkpoint, and materializes complete root totals plus exact category rows. Protocol v4/v5 category exactness is independent of the one returned extension row.

Once the immutable aggregate has been materialized successfully, the wrapper persists it to `storage_history_*`. The history SQLite write does not need to keep the NTFS semantic read lease open: a later rebuild cannot retroactively change the already-materialized observation. This keeps the cross-process lease duration limited to filesystem/index reading rather than extending it through an unrelated history transaction.

A failed or `SnapshotRequired` live analysis cannot create history. SQLite `BUSY`/`LOCKED` during save/query is translated to retryable service `Busy` rather than a non-retryable internal failure.

### Query validity

Historical observations are independently persisted and validated, so querying history intentionally does **not** require the current namespace checkpoint to remain valid. A previous trustworthy observation should still be readable while the live index needs a rebuild.

The current service implementation still requires the physical volume/root to be currently attached and resolvable. Offline/detached-volume history browsing remains a separate product decision.

The query reads only `storage_history_*`; it does not scan the filesystem or recalculate live Storage analytics.

## Desktop capture policy

`DesktopSearchEngine` owns automatic capture scheduling. Scheduling therefore starts with the native engine lifecycle and does not depend on the user opening Storage or the History view.

The first product policy captures only the **whole primary native volume**. Protocol v5 can represent arbitrary directory roots, but FileOp does not automatically turn every browsed folder into an implicit time series.

A capture is considered only when the primary native index reports `IsCurrent`. Background synchronization raises the engine state notification before releasing its native operation gate, so the scheduler yields briefly and then makes one non-blocking attempt.

Automatic capture acquires both desktop foreground/native gates with `WaitAsync(0)`. This means:

- an existing Search, Folders, Types or history-query operation wins immediately;
- desktop-gate contention defers capture for a short cooldown instead of queueing in front of the user;
- service/SQLite `Busy` uses a longer cooldown;
- `SnapshotRequired`, elevation-required or unavailable-volume states defer rather than creating an observation;
- a successful bucket suppresses further automatic captures for that UTC hour within the desktop session.

The service remains authoritative for bucket time and idempotency. A retry or app restart within the same UTC hour updates the same persisted row rather than creating a duplicate observation.

Automatic capture follows the named-pipe rule used elsewhere: it avoids transmitting while foreground work is already active, but once its request has been sent it is allowed to complete rather than being cancelled and faulting the reusable session.

## Accounting semantics

History persists the same logical-versus-physical model as live Storage analytics.

Logical bytes describe namespace names. Every visible hard-link name contributes logical bytes and a file-name count.

Physical allocation is counted once per stable `FileIdentity` by live analytics before the history store sees the observation. History never reinterprets or re-deduplicates those values.

`AllocatedBytes` remains nullable. The store rejects a root marked physically exact if category physical totals do not add back to that root. It also rejects a non-empty root marked physically unknown when every exact category row claims known physical allocation.

The store validates before persistence:

- non-negative root/category byte and count values;
- hard-link aliases cannot exceed namespace file counts;
- category enum values must be known;
- categories must be unique within an observation;
- logical/file/alias/type category totals must reconcile exactly to the root;
- known physical category totals must reconcile exactly to a known physical root.

Persisted observations are validated again on read. Invalid UTC ticks, unknown category enum values, impossible counts, duplicate categories, or root/category totals that no longer reconcile fail closed with `InvalidDataException` rather than becoming growth data for the UI.

## Delta semantics

`StorageHistoryDelta.Between` compares two observations for the same normalized root.

Root and category logical/file/alias/type deltas are signed. Growth is positive; shrinkage is negative.

Physical deltas are available only when both compared values are known. An absent category means a known zero baseline because the complete category set for that observation contains no files in that category. A present category with unknown physical allocation remains unknown, so the delta remains null rather than substituting logical bytes.

Physical uncertainty alone is not treated as evidence of change. If every logical/count metric is unchanged and physical allocation is unknown in both observations, that category is omitted from the changed-category list.

Category deltas are ordered by absolute physical change when available, otherwise absolute logical change. Equivalent root spellings compare using ordinal-ignore-case semantics.

## WinUI History view

Storage has a third **History** mode alongside Folders and Types. The History UI is a separate `StorageHistoryView` UserControl dynamically inserted into the existing Storage layout after `MainWindow.InitializeComponent`, so the reviewed `MainWindow.xaml` does not need another large rewrite.

History is native-only. If FileOp is using the profile crawler fallback, the UI explicitly says that fallback snapshots are not mixed into the native durable series.

The view loads at most 90 persisted observations by default. A loaded-empty result is cached like a populated result so recurring engine status notifications do not repeatedly issue an empty query.

### Usage timeline

Observations are sorted chronologically for display. The chart uses exactly one unit across the displayed series:

- physical allocation only if **every** displayed observation has known `AllocatedBytes`;
- otherwise logical size for every point.

Each row shows the local display time, proportional size against the largest displayed observation, current size, and signed change from the previous observation. The first point is labeled as the baseline.

### What grew?

The “What grew?” list compares only the newest two observations with `StorageHistoryDelta.Between`.

If the pair has an exact physical root delta, category rows use physical deltas; otherwise they use logical deltas consistently. Rows can be positive or negative and include file-name, hard-link-alias and extension-group count changes.

This is a latest-pair change explanation, not forecasting. FileOp does not infer a trend or future capacity date from two samples.

### UI lifecycle

History queries use the normal foreground desktop/service gates and therefore serialize safely with other requests on the single helper session. They do not cancel transmitted IPC.

The view tracks a generation and a separate active-load generation. Source changes invalidate stale results, while recurring background-sync state notifications cannot stop the progress indicator, re-enable Refresh mid-query, or erase an already loaded summary.

A successful automatic capture emits a desktop-engine notification. If History is visible, the cached series is invalidated and reloaded; otherwise it is simply marked stale until the next History visit.

## Retention

The persistence API exposes `PruneBeforeAsync(cutoff)` but protocol v5 does not expose deletion/pruning and does not choose a product retention cadence yet.

Retention policy should be measured against real user value and database sizes. A later service policy can prune old hourly observations without changing the stored snapshot contract.

## Validation without hosted Actions

`tools/verify_storage_history.py` covers persistence, Unicode root identity, rebuild survival, retention cascade, corruption rejection and randomized signed-delta reconciliation.

`tools/verify_storage_history_service.py` covers protocol/service behavior, including service-owned time, capture ordering, query independence from live checkpoint state, contention translation, host selection and database-key parity.

`tools/verify_storage_history_ui.py` adds desktop/UI checks without a Windows/.NET toolchain:

- UTC-hour due/cooldown properties across randomized timezone offsets;
- engine-owned scheduling independent of opening Storage;
- non-blocking automatic acquisition of foreground/native gates;
- whole-primary-volume capture scope;
- fallback-history exclusion;
- coherent physical-versus-logical timeline selection;
- latest-pair delta wiring;
- loaded-empty/source-generation and active-load-state guards;
- XAML parsing and UserControl event-handler resolution;
- dynamic insertion without modifying `MainWindow.xaml`.

The scheduler property model was exercised over 100,000 randomized cases in the available sandbox. `tools/test-local.ps1` runs the history UI verifier before the full .NET/Windows stack so a Windows development machine remains the compiler/runtime gate without GitHub Actions usage.

## Deliberate limitations

This slice does not yet:

- choose or execute automatic retention pruning;
- persist direct-folder historical breakdowns;
- offer explicit user-selected folder tracking;
- retain per-file historical events;
- infer deleted-file history from USN records;
- browse history for physically detached volumes;
- forecast future storage usage.

The next Storage-intelligence work should be measurement-driven. A likely higher-value next step is historical direct-folder contributors (“which folder caused the growth?”), but the indexed file-manager foundation is also now overdue and may provide broader product value before deeper history analytics.
