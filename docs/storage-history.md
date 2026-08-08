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

This is enough for future UI to answer “how much did this root grow?” and “which category drove the change?” while keeping persistence proportional to observations and categories rather than files and time.

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

A failed or `SnapshotRequired` live analysis cannot create history.

### Query validity

Historical observations are independently persisted and validated, so querying history intentionally does **not** require the current namespace checkpoint to remain valid. A previous trustworthy observation should still be readable while the live index needs a rebuild.

The initial service implementation still requires the physical volume/root to be currently attached and resolvable. Offline/detached-volume history browsing remains a separate product decision.

The query reads only `storage_history_*`; it does not scan the filesystem or recalculate live Storage analytics.

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

## Retention

The persistence API exposes `PruneBeforeAsync(cutoff)` but protocol v5 does not expose deletion/pruning and does not choose a product retention cadence yet.

Capture scheduling and retention policy should be measured against real user value and index sizes. A future desktop/service scheduler can choose when to request hourly-bucket captures and when to prune without changing the stored snapshot contract.

## Validation without hosted Actions

`tools/verify_storage_history.py` covers persistence, Unicode root identity, rebuild survival, retention cascade, corruption rejection and randomized signed-delta reconciliation.

`tools/verify_storage_history_service.py` adds protocol/service checks without a .NET SDK:

- UTC-hour bucket behavior across randomized timezone offsets;
- protocol v5 capture/query DTO and client/dispatcher wiring;
- absence of a client-controlled capture timestamp;
- capture ordering: validated live analytics before persistence;
- query independence from live analysis/checkpoint state;
- production indexer host selection;
- parity between the history wrapper and native backend's deterministic database-key formula.

`tests/FileOp.Windows.Tests/IndexingStorageHistoryProtocolTests.cs` adds the real typed named-pipe round trip for capture/query and request validation when the Windows/.NET gate is available.

`tools/test-local.ps1` runs the history and history-service verifiers before the full .NET/Windows test stack, so a Windows development machine can run the complete gate without GitHub Actions usage.

For this service slice, the hourly-bucket property was also exercised over 20,000 randomized timezone-offset cases in the available sandbox; Windows/.NET compilation remains delegated to the local Windows gate rather than being inferred from those Python checks.

## Deliberate limitations

This slice does not yet:

- schedule automatic captures from the desktop/background coordinator;
- render a history chart or “what grew?” list in WinUI;
- expose retention deletion through IPC;
- persist direct-folder historical breakdowns;
- retain per-file historical events;
- infer deleted-file history from USN records;
- browse history for physically detached volumes.

The next vertical slice should add a low-priority capture cadence to the existing desktop/native lifecycle and expose a read-only growth timeline using `GetStorageHistory`, without adding another scanner or cancelling in-flight helper exchanges.