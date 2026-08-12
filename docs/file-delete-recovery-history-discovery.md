# Read-only file delete recovery-history discovery

This slice makes durable recovery-sensitive delete history discoverable after restart. It remains inspection-only and does not authorize, retry, resume, or perform deletion.

## Why discovery is needed

The durable delete action-history barrier can persist an entry as `MutationStarted` before any future filesystem mutation. If the process stops after that durable barrier, the in-memory operation ID and user-authorization receipt are lost. `GetAsync(operationId)` alone is therefore insufficient for restart inspection: callers would need to know an operation ID that no longer exists in memory.

`IFileDeleteOperationRecoveryHistoryReader` adds bounded discovery without widening the writable action-history store. `SqliteFileDeleteOperationRecoveryHistoryReader` is a separate component and opens the existing history database with `SqliteOpenMode.ReadOnly` plus `PRAGMA query_only=ON`.

It creates no schema and exposes no begin/transition/complete API.

## Recovery candidate definition

An operation is discoverable only when at least one persisted entry is in one of these states:

- `MutationStarted`;
- `RecoveryRequired`.

That includes three restart-relevant shapes:

1. non-terminal `MutationStarted` because execution stopped after the durable barrier;
2. non-terminal `RecoveryRequired` because ambiguity was recorded but operation completion did not occur;
3. terminal `RecoveryRequired`, including a history completed while an entry remained `MutationStarted`.

The reader excludes pending-only operations, pre-barrier failures with no recovery-sensitive entry, and successful committed-only histories.

The SQL predicate is based on exact persisted entry states. Results are ordered by `started_utc_ticks DESC, operation_id DESC` and bounded to at most 4,096 operations, with a default limit of 100.

Schema validation, candidate IDs and complete histories are read inside one deferred SQLite read transaction so they share one snapshot. Each materialized `FileDeleteOperationActionHistory` must still validate structurally and must report `IsRecoverySensitive == true` and `DeleteMutationAuthorized == false` before it can be returned.

## Restart and consent boundary

The reader does not infer that a `MutationStarted` file was deleted. That state means only that the durable pre-mutation barrier was crossed before the previous process stopped reporting a settled outcome.

The returned history contains audit provenance such as authorization ID and timestamp, but it does not reconstruct the old session-only `FileDeleteOperationUserAuthorizationReceipt`, does not satisfy receipt reference binding, and does not authorize automatic retry or recovery mutation.

A terminal `RecoveryRequired` result is likewise diagnostic evidence, not permission to mutate. Any future recovery policy must be separately reviewed and must establish fresh user intent and a safe mutation-time binding.

## Read-only implementation boundary

The recovery reader deliberately does not extend the writable `IFileDeleteOperationActionHistoryStore`. This keeps existing store implementers source-compatible and makes restart discovery structurally read-only.

The SQLite reader:

- uses `SqliteOpenMode.ReadOnly`;
- enables `PRAGMA query_only=ON`;
- uses the provider/default private-cache behavior rather than opting into `Cache=Shared` on the WAL database;
- starts a deferred transaction and performs schema validation plus all candidate hydration inside that read snapshot;
- reads and validates schema version 1 but never creates or upgrades schema;
- uses only `SELECT` statements against delete action-history tables;
- exposes no `MarkMutationStartedAsync`, `CommitDeletedAsync`, `MarkMutationRecoveryRequiredAsync`, `CompleteAsync`, or other state transition;
- exposes no filesystem handle or mutation primitive.

A missing database is not initialized by the reader: opening it in read-only mode fails and leaves the path absent.

High-bit volume serial and file-reference values are reconstructed with unchecked signed-to-unsigned conversion, matching the durable writer's exact 64-bit encoding.

## Still out of scope

This slice adds no `DELETE` desired access, delete-capable lease, filesystem delete/recycle primitive, `FileOperationKind.Delete`, generic executor integration, automatic recovery action, production App/Windows/Indexer consumer, Storage cleanup UI, or indexing protocol change. Protocol remains v8.

## Validation without GitHub Actions

`tools/verify_file_delete_recovery_history_discovery.py` is standard-library-only. Its randomized model verifies exact recovery classification, deterministic newest-first ordering, bounds, exclusion of safe histories, and the absence of mutation/retry/deletion inference. Its real SQLite model exercises the exact recovery-state query and high-bit identity round-trip.

Focused .NET regressions create histories with the concrete writable SQLite store, dispose that writer to model a restart boundary, then reopen only the read-only recovery reader. They cover non-terminal `MutationStarted`, non-terminal `RecoveryRequired`, terminal `RecoveryRequired`, safe-state exclusion, high-bit identities, newest-first limits, invalid limits, missing-database no-create behavior, and disposed-reader refusal.

The verifier is part of `tools/test-local.ps1 -OfflineOnly`, so this slice can be validated without GitHub Actions or the .NET SDK. Native/.NET execution remains an additional local Windows validation layer when that runtime is available.
