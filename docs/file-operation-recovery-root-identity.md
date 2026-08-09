# Durable destination-root identity evidence for Copy recovery

## Purpose

Recovery evidence now distinguishes the destination **file object** from the destination **directory object** that originally contained it.

File identity plus matching primary-stream SHA-256 is not enough to prove namespace continuity. The original destination directory can be renamed/replaced and the same file object can later be moved back under the same textual path. In that case the file's canonical path, `FileIdentity` and content can all match while its parent namespace object is different.

This slice records the validated source/destination root identities at the action-history `BeginAsync` boundary and adds read-only destination-root inspection. It does not add any mutation authority.

## Durable capture

New action histories require canonical, non-reparse source and destination directories with stable identities. `SqliteFileOperationActionHistoryStore.BeginAsync` writes the operation row, the paired root identities and the entry rows in one SQLite transaction.

Root evidence is stored in the additive schema-v1 side table:

```text
file_operation_action_root_identities
  operation_id                 PRIMARY KEY / FK
  source_volume_serial
  source_file_reference
  destination_volume_serial
  destination_file_reference
```

The schema version remains `1`. Opening an older v1 database creates the side table idempotently. Existing operation rows with no side-table row remain readable with:

```text
SourceDirectoryIdentity      = null
DestinationDirectoryIdentity = null
HasVerifiedRootIdentities    = false
```

Root identity evidence is all-or-neither. New `BeginAsync` calls reject missing root identities before the history transaction writes anything. High-bit unsigned identity values retain their exact bit pattern through SQLite's signed INTEGER representation.

The source root is persisted symmetrically because it is part of the validated operation boundary, although this Copy-recovery slice consumes only destination-root evidence. No Move/source-removal recovery semantics are inferred from it.

## Read-only destination-root inspection

`FileOperationRecoveryInspector` exposes a separate `DestinationDirectory` observation with these statuses:

- `NoVerifiedIdentity` — legacy history has no durable destination-root identity;
- `Missing` — the recorded canonical destination directory is missing;
- `SameObject` — current canonical path and stable `FileIdentity` equal durable history;
- `DifferentObject` — the path names a different directory identity;
- `Redirected` — resolution reaches a different canonical location;
- `ReparsePoint` — the destination root is now a reparse point;
- `UnexpectedType` — the recorded directory now resolves to a file;
- `Inaccessible` — metadata inspection is denied;
- `Error` — the directory cannot be inspected reliably.

Legacy history is intentionally **not upgraded by observation**. If durable root identity is absent, the inspector reports `NoVerifiedIdentity` without pretending the current directory identity is historical evidence.

Leaf inspection still runs separately for diagnostics. This makes an important state representable: the destination root can be `DifferentObject` while the destination file itself remains `SameObject` because the same file was moved into a replacement directory.

## Content-verification gate

`FileOperationRecoveryContentVerifier` now requires all three prerequisites before calling the stable content reader:

```text
durable post-Copy SHA-256 exists
        AND
destination root observation == SameObject
        AND
destination file observation == SameObject
```

If root identity is missing or the root observation is anything other than `SameObject`, the item returns `DestinationRootNotVerified` and no file-content reader is invoked.

This prevents a known replaced-root state from being silently treated as a content match. It also means legacy histories without root identity remain evidence-insufficient even when their leaf identity and SHA-256 happen to match.

## Point-in-time limitation

The destination-root observation is still **point-in-time evidence**, not a held namespace lock. The metadata resolver does not keep the root handle alive through the later main-stream read. A directory could theoretically change after root inspection and before/during later evidence collection.

That limitation is intentional at this stage because no destructive action is authorized. A future final recovery/Undo authorization boundary must re-open and validate the destination root and keep the namespace-binding handle alive through the actual relative operation, rather than treating an earlier `SameObject` observation as permanent authority.

The same caution applies to hard links and other names for the file object: root identity improves namespace provenance but does not prove link topology or the absence of other names.

## Safety boundary

Root identity is recovery evidence only. It does not:

- set or change `UndoKind`;
- create a delete/Undo candidate;
- authorize deletion, move, replacement or any recovery mutation;
- verify ACLs, alternate data streams, extended attributes or other non-main-stream state;
- prove hard-link topology;
- expose Files UI execution/Undo wiring.

## Validation without hosted Actions

Run the dedicated root-evidence model/source guard:

```powershell
python tools/verify_recovery_root_identity.py --repo-root . --cases 50000
```

It is also wired into the normal Copy offline wrappers. Windows/.NET compilation and the native integration regressions remain in the shared deferred batch:

```bat
tools\test-windows-copy-local.cmd
```

## Next boundary

The remaining destructive-recovery design work still includes non-main-stream state policy and a **final handle-bound authorization protocol**. At that future boundary, FileOp must revalidate destination-root identity and the exact leaf object under handles that remain alive through the authorized relative mutation, then require explicit user authorization. This slice stops before any such mutation.