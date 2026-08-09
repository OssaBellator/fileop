# File Copy executor orchestration

## Purpose

`FileCopyOperationExecutor` defines the durable execution ordering for **file Copy only** on top of canonical validation and action history. `WindowsFileCopyMutationPrimitive` implements the production Windows mutation behind that orchestration, while the Files UI still does not instantiate or execute the Copy pipeline.

The mutation path does not use path-only `File.Copy`. The executor passes a `FileCopyMutationRequest` containing the freshly validated file item plus the exact freshly validated source/destination root objects. The Windows primitive uses those root identities to bind its native directory/file handles before creating anything.

## Mutation request and lease

A mutation request carries:

- the freshly validated `FileOperationExecutionValidationItem`;
- the fresh canonical source-directory path and stable identity;
- the fresh canonical destination-directory path and stable identity.

The primitive returns `IFileCopyMutationLease`, not a bare receipt. The lease exposes the immutable `FileCopyMutationReceipt` while retaining the source directory, destination directory, source file and created destination handles until the executor has:

```text
validated the receipt
        ↓
durably CommitCopy(destination identity)
        ↓
reported progress
        ↓
released the mutation lease
```

If receipt validation or durable Copy commit fails, the executor records `RecoveryRequired` while the lease is still held, then releases it. Lease disposal is cleanup rather than a durable state transition.

If the receipt has already passed executor validation and only `CommitCopyAsync` fails, the executor also persists that receipt's exact destination `FileIdentity` on the recovery entry. Earlier mutation, missing-lease and invalid-receipt failures do not have trusted destination identity evidence and therefore persist recovery without one.

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
       ↓ build FileCopyMutationRequest from fresh item + fresh roots
       ↓ acquire identity-bound Windows Copy mutation lease
       ↓ suppress automatic destination access/write timestamp updates
       ↓ copy bytes through bound handles + flush data
       ↓ apply supported basic metadata through the destination handle + flush metadata
       ↓ read + validate receipt
       ↓ durable CommitCopy(destination identity)
          └─ on commit failure, persist RecoveryRequired + verified receipt identity
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

The validator must return the exact plan instance requested. Fresh one-file validation must remain `Ready` with a missing destination and preserve the canonical source/destination root paths and identities, source file path and identity, and destination canonical path.

The executor passes those fresh roots directly to the mutation primitive. The Windows primitive reopens each canonical parent, verifies final path and `FileIdentity`, opens the source relative to the verified source-directory handle, and exclusively creates the destination relative to the verified destination-directory handle. A root swap, source replacement or destination collision after validation therefore fails conservatively rather than silently redirecting or overwriting the Copy.

Destination creation requests `FILE_WRITE_THROUGH`. FileOp suppresses automatic last-access/last-write updates on the destination handle before byte I/O, flushes copied data, applies the supported source basic metadata through that same destination handle, flushes again, and only then validates the destination identity. The mutation receipt must repeat the freshly validated source/destination canonical paths and source identity and provide a different destination identity.

## Basic metadata scope

The Windows primitive now preserves the deliberately narrow basic-metadata subset established by the merged metadata slice:

- creation time;
- last-access time;
- last-write time;
- read-only;
- hidden;
- system;
- archive;
- not-content-indexed.

Destination-owned `Temporary` and `Offline` values are retained when safe source attributes are overlaid. Compression, sparse, encryption, integrity, reparse semantics, ACLs, alternate data streams and extended attributes are not copied by this basic-metadata layer. Directory Copy remains unsupported.

## Recovery

After `MutationStarted`, native mutation failure, a missing/invalid mutation lease or receipt, or durable Copy-commit failure is recovery-sensitive. The executor attempts to persist entry and operation `RecoveryRequired`. If those follow-up writes also fail, the earlier durable `MutationStarted` record remains the restart-time signal that filesystem effects are uncertain.

A validated mutation receipt is a stronger boundary. If `CommitCopyAsync` fails after that receipt has passed path/source/destination identity validation, the executor persists its destination `FileIdentity` with the recovery entry. That identity is **recovery evidence only**. The entry remains `RecoveryRequired`, keeps `UndoKind.None`, and is never an `IsUndoCandidate`. Recovery code may use the identity later to prove which object was created, but it must not treat identity equality as permission to delete it.

Primitive failure, a missing lease, receipt access failure, or an invalid receipt never supplies destination identity to recovery. Those paths have not established trusted destination identity provenance.

A pre-mutation revalidation failure instead records ordinary `Failed`, because the mutation barrier was never crossed.

## Supported scope and safety

The executor rejects Move, directories, empty plans, unresolved/blocked validation and mismatched validator results. Initial Skip files never invoke the mutation primitive. Replace/overwrite remains absent.

The Files UI still does not instantiate or call `FileCopyOperationExecutor`, so the production Copy pipeline remains unreachable from normal application interaction. Directory Copy, Move and actual Undo remain out of scope.

## Validation without hosted Actions

Run the focused zero-Actions gate:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

The wrapper runs the existing offline suite, the executor lease-orchestration model, action-history persistence model, Windows mutation handle/root-binding model, metadata semantics and ABI guards without requiring hosted GitHub Actions.

On Windows with Python and .NET 10, run the focused compiler/native gate:

```bat
tools\test-windows-copy-local.cmd
```

That gate compiles Core/Windows and runs the focused action-history, Copy executor and native Windows regressions.

## Next boundary

The next recovery/Undo slice may consume the verified destination identity as one proof that a recovery path still points at the object created by FileOp. It still needs a no-user-change guard, explicit recovery policy and user-facing authorization before any deletion or replacement can occur. Identity evidence alone must never become destructive authority.
