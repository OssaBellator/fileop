# Durable file-operation action history

## Purpose

Canonical execution validation proves that a queued plan is safe enough to approach mutation, but it does not make a filesystem change recoverable. `IFileOperationActionHistoryStore` records durable operation/entry evidence, and `SqliteFileOperationActionHistoryStore` implements it with SQLite. `FileCopyOperationExecutor` consumes the store and delegates mutation through `IFileCopyMutationPrimitive`; `WindowsFileCopyMutationPrimitive` is the production Windows implementation. The Files UI still does not expose Run/Execute/Undo.

Schema v1 remains Copy-file-only for mutation state. Directory Copy and Move require separate recovery semantics.

## Begin and commit barriers

New action histories require canonical, non-reparse source/destination directories with stable `FileIdentity` values. `BeginAsync` atomically writes:

```text
operation row
+ source/destination root identities
+ initial entry rows
```

The executor later uses the familiar entry protocol:

```text
Pending
  ↓ durable MarkMutationStarted
MutationStarted
  ↓ identity-bound Copy mutation lease
  ↓ destination FileIdentity + post-Copy SHA-256 receipt
  ↓ durable CommitCopy
Committed
  ↓ progress
```

A validated receipt carries the created destination identity plus SHA-256 of the logical bytes successfully written through the bound Copy stream. `CommitCopyAsync` persists those proofs atomically with the committed entry transition. If only that durable commit fails, the same verified identity+fingerprint pair is persisted on `RecoveryRequired`; earlier mutation/lease/invalid-receipt failures persist neither.

`MutationStarted` remains the crash-safe ambiguity marker when no later durable effect proof exists.

## Root identity evidence

`FileOperationActionHistory` now exposes optional paired `SourceDirectoryIdentity` and `DestinationDirectoryIdentity` plus `HasVerifiedRootIdentities`. The pair is all-or-neither.

New production `BeginAsync` writes both validated root identities. Legacy schema-v1 operations may have neither and remain readable; they are explicitly weaker recovery evidence and are never upgraded by observing current roots.

The SQLite side table is additive:

```text
file_operation_action_root_identities
```

This is a **no migration** schema-v1 extension: initialization creates the table idempotently, and old operation rows simply have no matching side row. High-bit 64-bit identities round-trip via the existing unchecked unsigned/signed SQLite conversion.

The source identity is persisted symmetrically as part of the validated operation boundary. Current Copy recovery consumes destination-root evidence; this does not invent Move/source-removal recovery semantics.

## Entry states and undo candidates

Entry states remain `Pending`, `MutationStarted`, `Committed`, `Skipped`, `Failed`, and `RecoveryRequired`.

`DeleteCreatedDestination` is attached only to a successfully committed Copy entry. Such an entry can be an **undo candidate**, but an undo candidate is **not deletion authorization**. Stable identity is necessary but **not sufficient** to prove no relevant change occurred.

A `RecoveryRequired` entry always retains `UndoKind.None`, even when it carries verified destination identity/fingerprint evidence. That evidence is **recovery evidence only**.

## Recovery inspection layers

`FileOperationRecoveryInspector` now observes the durable destination-root identity separately from leaf identity. A replacement directory can therefore be detected even if the same file object is moved back under the same textual path and still has matching bytes.

For stronger current-content evidence, `FileOperationRecoveryContentVerifier` calls the stable SHA-256 reader only when:

1. durable post-Copy fingerprint exists;
2. destination-root observation is `SameObject`;
3. destination-file observation is `SameObject`.

The Windows content reader then re-proves leaf path/type/reparse/identity under the actual read handle and denies write/delete sharing while hashing. Root inspection remains point-in-time evidence; the root handle is not retained through that later read.

Therefore even root+leaf identity and matching SHA-256 remain **not sufficient** for destructive recovery. A final authorization boundary must re-open and hold namespace-binding handles through the actual relative mutation and define non-main-stream policy.

## Persistence model

Schema-v1 persistence now uses additive tables:

```text
file_operation_action_schema_info
file_operation_actions
file_operation_action_root_identities
file_operation_action_entries
file_operation_action_entry_content_fingerprints
```

The main operation row stores timestamps, kind/policy, captured roots, canonical roots and terminal state. The root side table stores the validated source/destination identities. Entry rows store source/destination paths, state/timestamps, source/destination file identities, undo metadata and failures. The fingerprint side table stores algorithm+digest keyed by `(operation_id, ordinal)`.

Writes are serialized per store instance. `BeginAsync` writes operation/root/entry rows in one transaction. Commit/recovery transitions condition on expected entry state and a non-terminal Copy operation. Recovery identity/fingerprint evidence is paired. SQLite uses WAL and `synchronous = FULL` because this data is recovery evidence rather than analytics state.

Older valid histories may lack the new side-table rows and continue loading conservatively with null evidence instead of being treated as corrupt.

## Failure semantics

Pre-mutation errors use `MarkEntryFailedBeforeMutationAsync`. After durable `MutationStarted`, failures use `MarkMutationRecoveryRequiredAsync`; if that follow-up persistence also fails, `MutationStarted` itself remains the restart-time recovery signal.

The validated-receipt/failed-commit path alone forwards verified destination identity+SHA-256 evidence to recovery. Root identities were already durably captured at `BeginAsync` from canonical validation.

## Safety boundary

The action-history contract/store contains no target `File.Copy`, `File.Move`, `File.Delete`, `Directory.Move`, `Directory.Delete`, or target-file write path. SQLite writes only FileOp's own recovery database.

Neither root identity, destination identity, SHA-256 evidence nor `MatchesRecordedMainStream` grants delete/Undo authority. The production Windows mutation primitive remains outside the history store, and the Files UI remains unwired for execution/Undo.

## Validation without hosted Actions

```powershell
python tools/verify_file_operation_action_history.py --repo-root . --cases 20000
python tools/verify_copy_content_fingerprint.py --repo-root . --cases 20000
python tools/verify_file_operation_recovery_inspection.py --repo-root . --cases 50000
python tools/verify_recovery_main_stream.py --repo-root . --cases 50000
python tools/verify_recovery_root_identity.py --repo-root . --cases 50000
pwsh -File tools/test-copy-executor-local.ps1
```

On Windows with .NET 10, `tools\test-windows-copy-local.cmd` remains the compiler/runtime source of truth and can be run later as the shared stacked batch.

## Next boundary

FileOp now has durable root identity, leaf identity and post-Copy primary-stream evidence plus read-only current observations. The next design boundary is non-main-stream policy and the final handle-bound authorization protocol: revalidate/hold destination-root and leaf bindings through the actual relative operation, define which metadata/ACL/ADS/EA/filesystem-specific changes invalidate recovery, and require explicit user authorization. Directory Copy and Move remain separate slices.