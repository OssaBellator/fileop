# Post-Copy content fingerprint evidence

## Purpose

Stable destination `FileIdentity` and canonical-location inspection can establish which filesystem object a recovery-sensitive path currently names, but they do not establish that the object's file contents are unchanged. This slice adds one stronger, still non-destructive proof: a SHA-256 fingerprint of the logical byte stream FileOp successfully wrote while creating a file Copy destination.

The fingerprint is recovery evidence only. It is **not deletion authorization**, does not change `UndoKind`, and does not make a recovery entry an Undo candidate.

## Capture boundary

`WindowsFileCopyMutationPrimitive` computes the destination content fingerprint inline while it copies through the already identity-bound source and destination handles.

For each source read:

1. FileOp writes the entire read chunk to the destination, handling partial `WriteFile` completion until every byte has been accepted.
2. Only after the whole chunk has been written does FileOp append that chunk to `IncrementalHash(SHA-256)`.
3. At end-of-file, the finalized digest is carried in the same `FileCopyMutationReceipt` as the validated source identity and newly created destination identity.

This makes the digest a fingerprint of the logical bytes successfully written through the **same bound copy stream**. There is no second path-based destination read and therefore no extra namespace lookup between Copy and evidence capture. A zero-length source produces the standard SHA-256 empty digest.

The existing ordering is otherwise unchanged: copied data is flushed, supported basic metadata is applied and flushed, the created destination identity is validated, and the mutation lease remains alive through durable commit/recovery and progress.

## Durable evidence

`FileContentFingerprint` currently supports one algorithm:

```text
SHA-256 / 64 hexadecimal characters
```

The action-history schema version remains v1. Fingerprints use an additive side table:

```text
file_operation_action_entry_content_fingerprints
```

It is keyed by `(operation_id, ordinal)` and references the existing action-history entry. This allows an existing schema-v1 database to be opened without rewriting `file_operation_action_entries`.

For a new successful Copy commit, destination identity and fingerprint are persisted in the same SQLite transaction as the `MutationStarted → Committed` transition.

If a mutation receipt passed executor validation but `CommitCopyAsync` fails, the recovery transition persists the verified destination identity and the verified fingerprint together in the same transaction. Recovery evidence is all-or-nothing: identity without fingerprint or fingerprint without identity is rejected.

Primitive failure, a missing mutation lease, receipt-access failure, an invalid receipt, or a plain `MutationStarted` entry receives neither verified destination identity nor fingerprint evidence.

## Legacy histories

Action-history databases created before this evidence existed may contain valid `Committed` or `RecoveryRequired` entries with no fingerprint side-table row. Those histories remain readable and expose `DestinationContentFingerprint == null`.

New production Copy receipts are stricter: the executor rejects a mutation receipt that does not contain a valid SHA-256 fingerprint. Legacy compatibility therefore does not weaken new Copy commit provenance.

## Safety boundary

A matching content fingerprint is **not a complete no-user-change proof**. This slice records the post-Copy content value but does not yet reopen a current destination under a race-safe identity-bound read lease and compare its bytes with that value.

Even a future content match would not, by itself, define policy for changes outside the main data stream, including metadata, ACLs, alternate data streams, extended attributes, compression/encryption/sparse state, or other filesystem semantics. Those concerns must be deliberately classified before destructive recovery is authorized.

No target-file delete, replacement, Move, recovery mutation, or Files UI execution/Undo path is added here.

## Validation without hosted Actions

Run the dedicated SHA-256 streaming/provenance/SQLite model and source guard:

```powershell
python tools/verify_copy_content_fingerprint.py --repo-root . --cases 20000
```

Run the full Copy zero-Actions gate:

```powershell
python tools/test-copy-executor-local.py --repo-root .
```

On Windows with .NET 10, the compiler/native source of truth remains:

```bat
tools\test-windows-copy-local.cmd
```

The focused native suite verifies a multi-chunk payload fingerprint and the zero-byte SHA-256 digest in addition to the existing handle-binding, recovery, action-history and metadata regressions.

## Next boundary

The next safe recovery slice should be a race-resistant **current destination content verifier**. It should combine the read-only recovery inspector's canonical path + stable identity proof with an identity-bound read handle, hash the current main data stream, and report whether it matches the durable post-Copy fingerprint while granting no destructive authority. Only after that should FileOp define the remaining non-content change policy and explicit user authorization required for actual recovery/Undo.