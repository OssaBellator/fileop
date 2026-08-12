# File delete action history and durable mutation barrier

This layer adds a **separate delete-only history schema** for future file deletion execution. It deliberately does not extend the existing Copy action-history tables, destination evidence, or Undo semantics.

It still performs no filesystem mutation and grants no delete authority.

## Why delete history is separate

The existing `FileOperationActionHistory` schema is intentionally Copy-file-oriented: it records destination paths/identities, Copy commit evidence and `DeleteCreatedDestination` Undo candidates. A file delete has no destination and needs different recovery semantics.

`FileDeleteOperationActionHistory` therefore has its own contracts and `SqliteFileDeleteOperationActionHistoryStore` uses independently versioned tables:

- `file_delete_action_history_schema_info` (schema version 1);
- `file_delete_actions`;
- `file_delete_action_entries`.

The delete store never reads or writes the existing `file_operation_actions` / `file_operation_action_entries` tables. There is no destination column, collision policy, Undo kind, or Copy evidence side table in the delete schema.

SQLite durability follows the reviewed action-history discipline: WAL mode, `synchronous=FULL`, foreign keys enabled, atomic begin inserts and exact-row transition predicates.

## Source-only durable provenance

`BeginAsync(...)` accepts the exact session-only `FileDeleteOperationUserAuthorizationReceipt` and snapshots audit/provenance data into durable history:

- plan/operation ID;
- authorization ID;
- queued, canonical-validation, authorization and history-start timestamps;
- source pane/tab provenance;
- canonical source-directory path + stable `FileIdentity`;
- every authorized file entry in original ordinal order;
- each canonical source path + stable `FileIdentity`.

The store preserves all 64 bits of both identity fields through SQLite signed INTEGER storage by unchecked `ulong ↔ long` conversion.

This durable record **does not persist a reusable authorization capability**. The authorization ID/timestamp explain which consent observation preceded the attempt; they do not reconstruct the in-memory receipt after restart, do not satisfy `IsBoundTo(...)`, and do not authorize automatic resume/retry.

## Per-entry durable barrier

Delete entries use a separate state set:

```text
Pending
MutationStarted
Committed
Failed
RecoveryRequired
```

The intended future ordering is:

```text
Pending
  -> persist MutationStarted durably
  -> acquire a future delete-capable same-handle lease
  -> perform one file mutation
  -> persist Committed while the mutation binding is still held
```

This PR implements only the persistence transitions. It does not implement the delete-capable lease or mutation.

`MarkMutationStartedAsync` succeeds only for an exact `Pending` entry under a non-terminal operation.

`CommitDeletedAsync` succeeds only for an exact `MutationStarted` entry **and** only when the supplied source `FileIdentity` matches the durable identity captured from the authorized file. That parameter is a future mutation-receipt binding check; calling `CommitDeletedAsync` itself does not prove a deletion happened.

A duplicate/out-of-order/wrong-identity transition affects zero rows and fails closed.

## Failure versus recovery ambiguity

`MarkFailedAsync` is only `Pending -> Failed`. It represents a failure before mutation began. Later untouched entries may remain `Pending`; they never crossed the barrier and are not recovery-sensitive.

`MarkMutationRecoveryRequiredAsync` is only `MutationStarted -> RecoveryRequired`. It represents a failure after the durable barrier where the eventual mutation outcome cannot safely be inferred by action history alone.

`CompleteAsync` does not accept a caller-supplied terminal label. It reloads the durable entry states inside its transaction and infers:

1. any `MutationStarted` or `RecoveryRequired` -> terminal `RecoveryRequired`;
2. otherwise any `Failed` -> terminal `Failed`;
3. otherwise every entry `Committed` -> terminal `Succeeded`;
4. otherwise completion is rejected because safe `Pending` work remains without a recorded failure.

Recovery-sensitive state therefore dominates failure/success and cannot be hidden by a later caller.

## Restart semantics

`RecoveryRequired` is a diagnostic/recovery-inspection state, not permission to mutate. A later restart path may inspect durable history, but it must not:

- recreate the old user-authorization receipt;
- automatically retry deletion;
- infer that a `MutationStarted` file was deleted;
- turn an authorization ID into `DeleteMutationAuthorized`;
- perform recovery mutation without a separately reviewed recovery policy and fresh user intent.

`FileDeleteOperationActionHistory.DeleteMutationAuthorized` remains hard-coded `false`.

## Scope still closed

This slice does **not** add:

- `FileOperationKind.Delete`;
- generic `IFileOperationExecutor` integration;
- a production delete executor/orchestrator;
- a delete-capable stability/mutation lease or `DELETE` access;
- a filesystem delete/recycle primitive;
- App/Windows/Indexer production consumers of the delete history store;
- Storage cleanup action UI;
- directory deletion;
- automatic recovery/resume;
- indexing-helper protocol changes.

Protocol remains v8.

## Validation

`tools/verify_file_delete_action_history.py` has two portable layers:

- randomized state-machine assertions for success, safe pre-barrier failure, unresolved post-barrier recovery, pending-only non-completion and exact identity-bound commit semantics;
- a real Python SQLite model (up to 5,000 cases) covering high-bit identity round-trip, exact `Pending -> MutationStarted`, duplicate transition refusal, wrong-identity commit refusal and exact-identity commit.

Focused .NET regressions exercise the concrete `Microsoft.Data.Sqlite` store with high-bit identities, success settlement, pre-barrier failure with untouched pending work, post-barrier recovery dominance, wrong-identity commit refusal, duplicate transitions and post-terminal refusal.

The history verifier is composed under the existing stability verifier, which is already reached through the single execution-validation local gate. Source guards require separate delete tables/versioning, FULL/WAL/foreign-key durability, exact transition and identity predicates, no Copy table/destination/Undo semantics, no mutation APIs, no production consumers, unchanged Copy/Move enum and protocol v8.

This environment has no .NET/Windows runtime, so the C# store/tests are not represented as compiled or executed here.