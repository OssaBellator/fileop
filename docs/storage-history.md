# Storage growth history

## Purpose

Storage history turns FileOp's current indexed Storage analysis into a compact time series without retaining per-file event history or rescanning the filesystem.

The first history slice stores aggregate observations only:

- root logical bytes;
- nullable root physical allocation;
- namespace file count;
- hard-link alias count;
- complete extension-group count;
- exact category rollups for the same observation.

This is enough for future UI to answer questions such as “how much did this root grow?” and “which category drove the change?” while keeping persistence proportional to observations and categories rather than files and time.

Folder-level historical contributors are intentionally left for a later slice. Per-file temporal logging is not part of this model.

## Storage model

`SqliteStorageHistoryStore` writes into the same per-volume SQLite database that contains FileOp's current metadata index. History uses three additive objects:

```text
storage_history_schema_info
storage_history_snapshots
storage_history_categories
```

The history objects have their own schema version. The existing core `schema_info` version continues to describe the current namespace/index schema. This keeps optional historical-analytics migrations independent without creating a second database.

A snapshot row is unique by case-insensitive root path plus UTC capture ticks. Repeating a write for the same root/timestamp updates the root row and replaces its category rows transactionally, making canonical time buckets idempotent for later scheduling.

Category rows are keyed by snapshot plus `StorageFileCategory`. Foreign-key cascade deletion removes category rows whenever retention deletes the owning snapshot.

## Rebuild semantics

The existing `SqliteFileIndex.ClearAsync` clears `files` and durable source checkpoints during a fresh namespace rebuild. It does not delete `storage_history_*` tables.

That is deliberate: a rebuild changes the current representation of the volume but should not erase trustworthy observations captured before the rebuild.

History is still scoped by root path inside one per-volume/root database. The native indexing architecture creates a fresh database when the same physical disk moves to a different root because current namespace paths are absolute. Historical observations therefore do not silently cross a drive-letter/root-identity change.

## Snapshot validity boundary

`SqliteStorageHistoryStore` is a persistence primitive, not a capture scheduler and not an authority on NTFS snapshot validity.

A future service integration must capture only while the existing indexing-service rules say the current namespace is readable:

1. resolve the requested volume/root;
2. take the process-local operation gate;
3. take the shared cross-process volume lease;
4. validate the durable checkpoint;
5. calculate exact storage/category analytics;
6. persist the observation before releasing the semantic read lease.

This keeps a historical row tied to a coherent indexed state rather than a partially rebuilt or invalid namespace.

The history write changes only `storage_history_*` tables. It must not mutate `files`, source checkpoints or NTFS journal state.

## Accounting semantics

History persists the same logical-versus-physical model as live Storage analytics.

Logical bytes describe namespace names. Every visible hard-link name contributes logical bytes and a file-name count.

Physical allocation is counted once per stable `FileIdentity` by live analytics before the history store sees the observation. History never reinterprets or re-deduplicates those numbers.

`AllocatedBytes` remains nullable. The store rejects a root marked physically exact if category physical totals do not add back to that root. It also rejects a non-empty root marked physically unknown when every exact category row claims known physical allocation.

The store validates before persistence:

- non-negative root/category byte and count values;
- hard-link aliases cannot exceed namespace file counts;
- category enum values must be known;
- categories must be unique within an observation;
- logical/file/alias/type category totals must reconcile exactly to the root;
- known physical category totals must reconcile exactly to a known physical root.

Corrupt persisted category enum values are rejected on read rather than mapped to an arbitrary display category.

## Delta semantics

`StorageHistoryDelta.Between` compares two observations for the same normalized root.

Root and category logical/file/alias/type deltas are signed. Growth is positive; shrinkage is negative.

Physical deltas are available only when both compared values are known. An absent category means a known zero baseline because the complete category set for that observation contains no files in that category. A present category with unknown physical allocation remains unknown, so the delta remains null rather than substituting logical bytes.

Category deltas are ordered by absolute physical change when available, otherwise absolute logical change. This provides a useful default for a future “what grew?” surface without changing the underlying signed values.

Equivalent root spellings such as `C:\Data` and `c:\data\` compare as the same indexed root.

## Retention

The persistence API exposes `PruneBeforeAsync(cutoff)` but deliberately does not choose a product retention cadence yet.

Scheduling policy belongs with service integration because capture interval, idle-time behavior and retention should be measured against real index sizes and user value. A later slice can choose a canonical time bucket and retention horizon without changing the stored snapshot contract.

Retention is global to the history store/database and cascades category rows transactionally through SQLite foreign keys.

## Validation without hosted Actions

`tools/verify_storage_history.py` uses only Python's standard library. Its executable checks cover:

- history schema creation;
- case-insensitive root lookup;
- same-root/same-timestamp overwrite identity;
- namespace-clear survival;
- retention cascade behavior;
- randomized signed-delta reconciliation across all 12 categories.

Repository mode additionally guards source-level invariants: the history store and tests must exist, the current `SqliteFileIndex.ClearAsync` must not erase history, and history schema versioning must remain independent of the core index schema.

`tools/test-local.ps1` invokes the history verifier before the full .NET/Windows test stack, so a Windows developer machine can run the complete gate without GitHub Actions usage.

## Deliberate limitations

This slice does not yet:

- schedule automatic captures;
- expose history through the indexing-service protocol;
- render a chart or growth list in WinUI;
- persist direct-folder historical breakdowns;
- retain per-file historical events;
- infer deleted-file history from USN records.

The next vertical slice should integrate capture/query through the indexing service under the existing checkpoint and cross-process lease rules, then add a read-only WinUI growth timeline from those trusted observations.
