# File Copy executor orchestration

## Purpose

`FileCopyOperationExecutor` defines the durable execution ordering for **file Copy only** on top of canonical validation and action history. `WindowsFileCopyMutationPrimitive` implements the production Windows mutation behind that orchestration, while the Files UI still does not instantiate or execute the Copy pipeline.

The mutation path does not use path-only `File.Copy`. The executor passes a `FileCopyMutationRequest` containing the freshly validated file item plus the exact freshly validated source/destination root objects. The Windows primitive uses those root identities to bind its native directory/file handles before creating anything.

## Mutation request and lease

A mutation request carries the freshly validated file item plus the fresh canonical source/destination directory objects and stable identities.

The primitive returns `IFileCopyMutationLease`, not a bare receipt. The lease exposes the immutable `FileCopyMutationReceipt` while retaining source/destination directory and file handles until the executor has:

```text
validated receipt identity/path + SHA-256 evidence
        ↓
durably CommitCopy(destination identity + fingerprint)
        ↓
reported progress
        ↓
released the mutation lease
```

A production receipt is valid only when it contains the freshly validated source/path provenance, a distinct created destination identity, and a valid SHA-256 `DestinationContentFingerprint`.

If receipt validation or durable Copy commit fails, the executor records `RecoveryRequired` while the lease is still held, then releases it. Lease disposal is cleanup rather than a durable state transition.

If the receipt has already passed executor validation and only `CommitCopyAsync` fails, the executor persists that receipt's exact destination `FileIdentity` and content fingerprint together on the recovery entry. Earlier mutation, missing-lease and invalid-receipt failures persist neither proof.

## Ordering

For one supported Copy-file plan:

```text
full canonical validation
  ↓
durable Begin action history
  ↓
Running
  ↓
for each entry
  ├─ initial Skip → progress only
  └─ initial Ready
       ↓ fresh one-entry canonical validation
       ↓ compare canonical root identities and paths
       ↓ compare source identity/path and destination canonical path
       ↓ cancellation safe-boundary check
       ↓ durable MarkMutationStarted
       ↓ acquire identity-bound Windows Copy mutation lease
       ↓ suppress automatic destination access/write timestamp updates
       ↓ copy bytes through bound handles
          └─ after each read chunk is fully written, append it to SHA-256
       ↓ flush copied data
       ↓ apply supported basic metadata through destination handle + flush metadata
       ↓ validate created destination identity
       ↓ return receipt with destination identity + SHA-256 fingerprint
       ↓ executor validates receipt
       ↓ durable CommitCopy(identity + fingerprint)
          └─ on commit failure, persist RecoveryRequired + same verified evidence pair
       ↓ report progress
       ↓ release mutation lease
       ↓ cancellation safe-boundary check
  ↓
durable terminal history
```

Progress for a mutated file is never reported before `CommitCopyAsync` succeeds. Progress callbacks are advisory and cannot rewrite durable state.

## Cancellation

Cancellation uses `RequestCancellationAsync`, not an arbitrary `ExecuteAsync` token. Initial and fresh read-only validation receive an internal cancellation token.

Before `MutationStarted`, cancellation can stop immediately. Once `MutationStarted` is durable, cancellation is intentionally not passed into the mutation primitive. One file must settle to durable Commit or recovery before the request can take effect. If cancellation arrives during mutation, the current lease remains held through commit/recovery and progress, then is released before cancellation settles.

A request arriving after the final file has durably committed settles as success rather than retroactively relabelling completed work as cancelled.

## Revalidation and native binding

The validator must return the exact plan instance requested. Fresh one-file validation must remain `Ready` with a missing destination and preserve canonical roots, source path/identity and destination canonical path.

The executor passes those fresh roots directly to the mutation primitive. The Windows primitive reopens each canonical parent, verifies final path and `FileIdentity`, opens the source relative to the verified source-directory handle, and exclusively creates the destination relative to the verified destination-directory handle. A root swap, source replacement or destination collision after validation therefore fails conservatively rather than silently redirecting or overwriting the Copy.

Destination creation requests `FILE_WRITE_THROUGH`. FileOp suppresses automatic last-access/last-write updates before byte I/O. The content fingerprint is computed from exactly the logical bytes successfully written through that same bound stream; there is no second path-based destination read. FileOp then flushes data, applies the supported source basic metadata through the same destination handle, flushes again, validates destination identity, and returns the receipt while all binding handles remain leased.

## Basic metadata scope

The Windows primitive preserves the deliberately narrow basic-metadata subset established by the merged metadata slice:

- creation time;
- last-access time;
- last-write time;
- read-only;
- hidden;
- system;
- archive;
- not-content-indexed.

Destination-owned `Temporary` and `Offline` values are retained when safe source attributes are overlaid. Compression, sparse, encryption, integrity, reparse semantics, ACLs, alternate data streams and extended attributes are not copied by this basic-metadata layer. Directory Copy remains unsupported.

## Recovery and content evidence

After `MutationStarted`, native mutation failure, a missing/invalid mutation lease or receipt, or durable Copy-commit failure is recovery-sensitive. The executor attempts to persist entry and operation `RecoveryRequired`. If those follow-up writes also fail, the earlier durable `MutationStarted` record remains the restart-time signal that filesystem effects are uncertain.

A validated mutation receipt is a stronger boundary. If `CommitCopyAsync` fails after path/source/destination identity and SHA-256 evidence validation, the executor persists destination identity + fingerprint together. That pair is **recovery evidence only**. The entry remains `RecoveryRequired`, keeps `UndoKind.None`, and is never an `IsUndoCandidate`.

`FileOperationRecoveryInspector` first reports whether a recovery-sensitive destination still resolves to the recorded object/location. `FileOperationRecoveryContentVerifier` then performs a separate read-only main-stream comparison only for `SameObject` entries that have durable SHA-256 evidence. On Windows, `WindowsFileContentFingerprintReader` re-proves final canonical path, ordinary-file type, non-reparse status and exact `FileIdentity` under the actual read handle, denies write/delete sharing while hashing, and compares the current primary-stream SHA-256 with the durable post-Copy value.

A `MatchesRecordedMainStream` result is stronger evidence than identity/location alone, but it is still **not a complete no-user-change proof**. Metadata, ACLs, alternate data streams, extended attributes and other non-main-stream state remain outside this comparison.

Primitive failure, missing lease, receipt access failure or invalid receipt never supplies verified identity/fingerprint evidence to recovery.

A pre-mutation revalidation failure instead records ordinary `Failed`, because the mutation barrier was never crossed.

## Supported scope and safety

The executor rejects Move, directories, empty plans, unresolved/blocked validation and mismatched validator results. Initial Skip files never invoke the mutation primitive. Replace/overwrite remains absent.

Neither a destination identity, a content fingerprint, nor a later `MatchesRecordedMainStream` result is delete/Undo authorization. The Files UI still does not instantiate or call `FileCopyOperationExecutor`, so the production Copy pipeline remains unreachable from normal application interaction. Directory Copy, Move and actual Undo remain out of scope.

## Validation without hosted Actions

Run the focused zero-Actions gate:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

The wrapper runs action-history, recovery inspection, SHA-256 content evidence, stable recovery main-stream verification, executor orchestration, Windows handle-binding, metadata semantics and ABI guards without hosted GitHub Actions.

On Windows with Python and .NET 10, run the batch gate when convenient:

```bat
tools\test-windows-copy-local.cmd
```

That gate compiles Core/Windows and runs the focused action-history, recovery-inspection/content-verification, Copy executor and native Windows regressions.

## Next boundary

The remaining destructive-recovery prerequisite is no longer the primary data stream itself. A later design must decide which non-main-stream state changes matter (metadata, ACLs, ADS, EAs and filesystem-specific semantics), define the final race-safe authorization boundary, and require explicit user authorization before deletion or replacement. The current verifier remains read-only evidence only.
