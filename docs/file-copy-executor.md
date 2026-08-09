# File Copy executor orchestration

## Purpose

`FileCopyOperationExecutor` defines durable execution ordering for **file Copy only**. `WindowsFileCopyMutationPrimitive` provides the production identity-bound mutation, while the Files UI still does not instantiate or execute the pipeline.

The executor never substitutes path-only `File.Copy`. Fresh validation supplies canonical source/destination roots with stable identities, and the mutation request carries those exact validated root objects into the Windows primitive.

## Durable root boundary

Before mutation, `SqliteFileOperationActionHistoryStore.BeginAsync` now durably records the validated source and destination root `FileIdentity` values alongside the operation and initial entries. The root pair is written in the same transaction as the begin barrier.

The executor already revalidates each ready entry immediately before mutation and requires the fresh source/destination root objects to match the initial validation. The Windows mutation primitive then reopens and validates those roots under handles before relative source open and exclusive destination creation. Thus a successful/receipt-producing mutation is tied back to the root identity durably captured at `BeginAsync`.

Legacy histories without root evidence remain readable but are not promoted into the stronger recovery proof.

## Mutation lease and receipt

The primitive returns an `IFileCopyMutationLease`, retaining source/destination directory and file handles through receipt validation, durable commit/recovery and progress.

A production receipt contains validated path/source provenance, a distinct created destination identity, and SHA-256 `DestinationContentFingerprint` evidence. The ordering remains:

```text
fresh validation
  ↓
durable MarkMutationStarted
  ↓
identity-bound Windows mutation lease
  ↓
copy bytes + inline SHA-256
  ↓
data flush
  ↓
apply supported basic metadata + metadata flush
  ↓
validate created destination identity
  ↓
validate receipt
  ↓
durable CommitCopy(destination identity + fingerprint)
  ↓
progress
  ↓
release mutation lease
```

If only `CommitCopyAsync` fails after a valid receipt, recovery receives the same verified destination identity+fingerprint pair. Earlier mutation/missing-lease/invalid-receipt failures receive neither. Root identities are already present from `BeginAsync`.

Progress remains advisory and is never reported for a mutated entry before durable commit succeeds.

## Cancellation and revalidation

Cancellation is honored only at safe boundaries. Once `MutationStarted` is durable, the current file settles to committed/recovery state and releases its mutation lease before cancellation can stop later work.

Fresh validation must preserve root canonical paths/identities, source canonical path/identity and missing destination canonical path. A root swap or source/destination change fails conservatively before mutation or inside the Windows handle-binding primitive.

## Copy content and metadata

The mutation computes SHA-256 from exactly the logical bytes successfully written through its bound stream; it does not reopen the destination by path for hashing. The supported basic metadata subset remains creation/access/write times plus read-only, hidden, system, archive and not-content-indexed, with destination-owned Temporary/Offline retention. ACLs, alternate data streams, EAs, compression/encryption/sparse/integrity and directory Copy remain outside this layer.

## Recovery evidence

Recovery inspection is now layered:

1. `FileOperationRecoveryInspector` compares durable destination-root identity with the current canonical directory object, and separately inspects the recovery leaf.
2. `FileOperationRecoveryContentVerifier` requires durable SHA-256 **plus** root `SameObject` **plus** leaf `SameObject` before invoking the stable content reader.
3. `WindowsFileContentFingerprintReader` re-proves the leaf under its actual read handle and compares current main-stream SHA-256.

This catches a namespace case that leaf identity/content alone misses: the original destination directory can be replaced while the same file object is moved back into the replacement directory. The leaf may remain `SameObject`, but the root is `DifferentObject`, so content verification returns `DestinationRootNotVerified` without reading bytes.

The root observation is still point-in-time. Its handle is not retained through the later read, so this stack is **not a final race-safe authorization boundary**.

A `MatchesRecordedMainStream` result plus matching root/leaf identity is stronger recovery evidence, but still **not a complete no-user-change proof**. Metadata, ACLs, alternate data streams, EAs, hard-link topology and other filesystem state remain outside the evidence policy.

## Supported scope and safety

The executor rejects Move, directories, empty plans, blocked validation and mismatched validator results. Initial Skip entries do not invoke mutation.

Neither root identity, destination identity, content fingerprint nor a later content match grants delete/Undo authority. RecoveryRequired remains non-destructive. Replace/overwrite, actual Undo and Files UI execution wiring remain absent.

## Validation without hosted Actions

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

The zero-Actions wrapper now covers action history, recovery inspection, fingerprint evidence, stable main-stream verification, destination-root identity evidence, executor orchestration, Windows handle-binding, metadata semantics and ABI guards.

Windows compiler/native validation remains batched for the stacked work:

```bat
tools\test-windows-copy-local.cmd
```

## Next boundary

The remaining destructive-recovery work is the final handle-bound authorization protocol plus non-main-stream policy. A future action must reopen/revalidate the destination root and exact leaf, retain those namespace-binding handles through an authorized relative operation, define invalidating metadata/ACL/ADS/EA/filesystem-specific changes, and require explicit user authorization.