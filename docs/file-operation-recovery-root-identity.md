# Durable destination-root identity evidence for Copy recovery

## Purpose

Recovery evidence distinguishes the destination **file object** from the destination **directory object** that originally contained it.

File identity plus matching primary-stream SHA-256 is not enough to prove namespace continuity. The original destination directory can be renamed/replaced and the same file object can later be moved back under the same textual path. In that case the file's canonical path, `FileIdentity` and content can all match while its parent namespace object is different.

The root-evidence layer records the validated source/destination root identities at the action-history `BeginAsync` boundary and adds read-only destination-root inspection. The later root-bound content reader consumes that evidence without adding mutation authority.

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

The schema version remains `1`. This is an additive **no migration** change: opening an older v1 database creates the side table idempotently. Existing operation rows with no side-table row remain readable with:

```text
SourceDirectoryIdentity      = null
DestinationDirectoryIdentity = null
HasVerifiedRootIdentities    = false
```

Root identity evidence is all-or-neither. New `BeginAsync` calls reject missing root identities before the history transaction writes anything. High-bit unsigned identity values retain their exact bit pattern through SQLite's signed INTEGER representation.

The source root is persisted symmetrically because it is part of the validated operation boundary, although current Copy-recovery evidence consumes only destination-root identity. No Move/source-removal recovery semantics are inferred from it.

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

Leaf inspection still runs separately for diagnostics. This makes an important state representable: the destination root can be `DifferentObject` while the destination file itself remains `SameObject` because the same file object was moved into a replacement directory.

## Content-verification gate

`FileOperationRecoveryContentVerifier` requires all three prerequisites before calling the root-bound content reader:

```text
durable post-Copy SHA-256 exists
        AND
destination root observation == SameObject
        AND
destination file observation == SameObject
```

If root identity is missing or the root observation is anything other than `SameObject`, the item returns `DestinationRootNotVerified` and no file-content reader is invoked.

When those prerequisites pass, the verifier does **not** treat the earlier root observation as a lasting lock. It creates a `FileContentFingerprintReadRequest` containing the observed canonical root path plus durable root identity and the recorded leaf path plus durable leaf identity. `WindowsRootBoundFileContentFingerprintReader` reopens the root, verifies it again, opens the leaf relative to that root with `NtCreateFile`, and keeps both handles alive through hashing and post-read checks.

This means a root replacement after metadata inspection is detected at the actual byte-read boundary as `DestinationRootChanged` rather than silently producing a matching SHA-256 from a file under a replacement directory.

## Time-bound nature of the evidence

The metadata inspection itself remains **point-in-time evidence**. The root-bound reader closes the inspection-to-hash namespace gap by independently re-establishing and holding the root/leaf bindings while the content observation is made.

That still does not create a durable lock for some later destructive action. Once the read handles are released, the namespace can change again. A future final recovery/Undo authorization boundary must therefore perform its own root/leaf validation and keep those handles alive through the actual relative mutation rather than reusing an earlier `SameObject` or `MatchesRecordedMainStream` result as authority.

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

Run the root-evidence and root-bound main-stream model/source guards:

```powershell
python tools/verify_recovery_root_identity.py --repo-root . --cases 50000
python tools/verify_recovery_main_stream.py --repo-root . --cases 50000
```

They are wired into the normal Copy offline wrappers. Windows/.NET compilation and native integration regressions remain in the shared deferred batch:

```bat
tools\test-windows-copy-local.cmd
```

## Next boundary

The remaining destructive-recovery design work still includes non-main-stream state policy and a **final handle-bound authorization protocol**. At that future boundary, FileOp must revalidate destination-root identity and the exact leaf object under handles that remain alive through the authorized relative mutation, then require explicit user authorization. The current recovery readers stop before any such mutation.
