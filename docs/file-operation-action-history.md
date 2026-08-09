# Durable file-operation action history

## Purpose

Canonical execution validation proves that a queued plan is safe enough to approach a mutation boundary, but it does not make a filesystem change recoverable. FileOp therefore records durable per-entry action history before and after each supported mutation boundary so a process restart can distinguish a completed effect from an uncertain one.

`IFileOperationActionHistoryStore` defines that persistence boundary and `SqliteFileOperationActionHistoryStore` implements it with SQLite. `FileCopyOperationExecutor` consumes the store and delegates filesystem mutation through `IFileCopyMutationPrimitive`; `WindowsFileCopyMutationPrimitive` provides the production Windows handle-bound implementation behind that executor. The Files UI still does not expose Run/Execute/Undo.

Action-history schema v1 is intentionally **Copy-file-only for mutation state**. A directory or Move entry may be recorded as `Skipped`, but a ready directory or ready Move entry is rejected before the history transaction can commit. Recursive directory Copy needs child-level recovery/commit semantics, and Move needs source-removal/cross-volume partial-failure semantics; neither is inferred from the file-Copy protocol.

## Commit barrier

The Copy executor uses durable history as part of each file commit protocol:

```text
canonical execution validation
        ↓
Begin action history
        ↓
Pending
        ↓ durable MarkMutationStarted
MutationStarted
        ↓ identity-bound Copy mutation lease
        ↓ capture destination identity + SHA-256 content fingerprint
        ↓ durable CommitCopy
Committed
        ↓ report entry progress
```

The important barrier is after the filesystem mutation and before progress is reported. A valid mutation receipt carries both the newly created destination `FileIdentity` and a SHA-256 fingerprint of the logical bytes written through the bound Copy stream. `CommitCopyAsync` persists those two proofs atomically with the `Committed` transition.

If `CommitCopyAsync` cannot durably finalize that record after the receipt passed executor validation, the executor retains the exact destination identity **and** fingerprint together while the entry settles as `RecoveryRequired`. If the mutation primitive fails, the lease is missing, or the receipt is invalid, neither proof is trusted or persisted.

A process crash while an entry remains `MutationStarted` is intentionally ambiguous. The record says mutation was allowed to begin but there is no durable proof of its final effect. On restart, `RequiresRecovery` is therefore true and automatic replay/undo must not guess what happened.

## Entry states

Action entries use these durable states:

- `Pending` — execution validation said a file destination was missing and mutation has not been declared started;
- `MutationStarted` — the durable pre-mutation barrier was crossed; a crash from here is recovery-sensitive and no verified destination evidence is available;
- `Committed` — file Copy completed and the created destination identity was durably captured; new commits also carry SHA-256 content evidence;
- `Skipped` — execution validation selected non-destructive Skip; no mutation is expected;
- `Failed` — failure occurred before mutation began, so no recovery ambiguity is introduced;
- `RecoveryRequired` — mutation began but the effect cannot be represented as a clean committed Copy. The validated-receipt/failed-commit path may carry the paired destination identity + fingerprint evidence.

An operation can end `Succeeded` only when every entry is `Committed` or `Skipped`. If any entry is `MutationStarted` or `RecoveryRequired`, the operation may terminate only as `RecoveryRequired`.

## Copy undo candidates

This schema defines one recovery hint:

```text
DeleteCreatedDestination
```

It is attached only by `CommitCopyAsync`, and only after a file entry crosses `Pending → MutationStarted`. A `Pending` entry exists only when canonical execution validation saw the destination leaf as missing. The history therefore records a destination FileOp intended to create rather than an existing object it replaced.

A committed file with the exact destination `FileIdentity` becomes an **undo candidate**, not deletion authorization. Identity equality is necessary to prove the path still names the same filesystem object, but it is **not sufficient** to prove the user or another program has not modified that object's contents or metadata since Copy. SHA-256 records FileOp's post-Copy main-stream content, and the separate read-only recovery content verifier can compare current primary-stream bytes under a stable identity-bound read handle, but non-main-stream change policy remains intentionally undefined.

A `RecoveryRequired` entry is different. Even when it carries verified destination identity + fingerprint evidence, its `UndoKind` remains `None`: recovery evidence only does not authorize deletion or make the entry an undo candidate. Primitive failures, missing leases and invalid receipts continue to persist recovery without verified destination evidence.

`FileOperationRecoveryInspector` consumes recovery-sensitive Copy history through the metadata-only canonical resolver. It classifies the current destination as missing, same identity/location, different identity, redirected, reparse, unexpected type, inaccessible or error. `FileOperationRecoveryContentVerifier` may then hash only a `SameObject` entry that also has durable fingerprint evidence. On Windows the content reader re-proves canonical location, ordinary-file type, non-reparse state and exact `FileIdentity` under the read handle and denies write/delete sharing while hashing. Both stages are read-only and neither changes `UndoKind` or authorizes deletion.

Directory entries are never undo candidates in schema v1. Existing destinations handled by `Skip` never receive undo metadata. Replace/overwrite remains unsupported. Move does not receive Copy-style undo candidates because Move has additional source-removal and cross-volume partial-failure semantics that are not designed yet.

## Persistence model

The SQLite store uses independently versioned additive tables:

```text
file_operation_action_schema_info
file_operation_actions
file_operation_action_entries
file_operation_action_entry_content_fingerprints
```

The operation row stores queued/validated/started/completed timestamps, operation/collision kind, captured roots, canonical roots and terminal state. Each entry stores its original source metadata, canonical source/destination paths, durable state timestamps, source/destination identities, undo kind and structured failure information. The additive fingerprint side table stores algorithm + digest keyed by `(operation_id, ordinal)`.

Recovery destination identity continues to use the nullable `destination_volume_serial` and `destination_file_reference` columns. Content evidence uses the side table rather than changing the main entry layout. This remains additive schema-v1 behavior and requires **no migration** or schema-version increment; opening an older v1 database creates the side table idempotently.

Older valid committed/recovery entries may have no fingerprint row and remain readable with `DestinationContentFingerprint == null`. New production Copy commits are stricter: a receipt without SHA-256 content evidence is rejected before durable commit.

Writes are serialized per store instance and state transitions use conditional SQL predicates against both the expected entry state and a non-terminal operation. Destination identity and fingerprint evidence are written in the same transaction as the associated commit/recovery transition. Recovery evidence is paired: identity without fingerprint or fingerprint without identity is rejected.

SQLite uses WAL plus `synchronous = FULL` for this recovery log. The stronger synchronous setting is intentional: this metadata is recovery evidence, not an analytics cache.

Stable 64-bit identity values preserve their raw bit pattern through SQLite's signed 64-bit INTEGER representation (`unchecked` ulong/long conversion), so high-bit file references round-trip exactly for both committed and recovery identities.

## Failure semantics

`MarkEntryFailedBeforeMutationAsync` transitions `Pending → Failed` for errors that occur before the mutation boundary. Such an entry does not by itself require recovery.

After `MutationStarted`, a failure must use `MarkMutationRecoveryRequiredAsync`. It is intentionally not a normal Failed entry because the filesystem may already have changed partially. The method accepts optional verified destination identity + content fingerprint evidence, but requires them as a pair. `FileCopyOperationExecutor` supplies the pair only when a validated mutation receipt exists and `CommitCopyAsync` fails; earlier native/lease/receipt failures pass neither. If the process crashes before the explicit recovery transition can be persisted, the durable `MutationStarted` state itself remains the restart-time recovery signal.

## Safety boundary

The action-history contract and SQLite store contain no `File.Copy`, `File.Move`, `File.Delete`, `Directory.Move`, `Directory.Delete`, or target-file write path. SQLite persistence necessarily writes FileOp's own action-history database; that application metadata write is separate from mutating the user's queued source/destination namespace.

The production Windows mutation primitive is handle-bound and remains outside the history store. Neither destination identity, SHA-256 evidence, nor a later `MatchesRecordedMainStream` result is deletion authorization. The Files UI still does not expose Run/Execute/Undo.

## Validation without hosted Actions

Run the standard-library action-history model and source guard directly:

```powershell
python tools/verify_file_operation_action_history.py --repo-root . --cases 20000
```

Run the Copy fingerprint model/source guard:

```powershell
python tools/verify_copy_content_fingerprint.py --repo-root . --cases 20000
```

Run the read-only recovery inspection model/source guard:

```powershell
python tools/verify_file_operation_recovery_inspection.py --repo-root . --cases 50000
```

Run the stable recovery main-stream verifier:

```powershell
python tools/verify_recovery_main_stream.py --repo-root . --cases 50000
```

Run the complete offline gate without consuming GitHub Actions quota:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

For the Copy orchestration layer, run:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

On Windows with .NET 10, `tools\test-windows-copy-local.cmd` remains the compiler/runtime source of truth for Core/Windows and the focused native Copy regressions; these checks may be batched for the stacked Copy-recovery work.

## Next boundary

FileOp can now compare a recovery-inspected destination's primary data stream with durable post-Copy SHA-256 evidence under a stable read handle. The next design boundary is the **non-main-stream change policy and final authorization protocol**: decide what metadata/ACL/ADS/EA/filesystem-specific changes invalidate destructive recovery, revalidate at the final action boundary, and require explicit user authorization. Directory Copy and Move remain separate later slices.
