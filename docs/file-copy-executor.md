# File Copy executor orchestration

## Purpose

`FileCopyOperationExecutor` defines the durable execution ordering for **file Copy only** on top of canonical validation and action history. `WindowsFileCopyMutationPrimitive` now implements the narrow Windows byte-copy mutation behind that orchestration, but the Files UI still does not instantiate or execute the Copy pipeline.

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
       ↓ read + validate receipt
       ↓ durable CommitCopy(destination identity)
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

The mutation receipt must repeat the freshly validated source/destination canonical paths and source identity and must provide a different destination identity.

## Recovery

After `MutationStarted`, native mutation failure, a missing/invalid mutation lease or receipt, or durable Copy-commit failure is recovery-sensitive. The executor attempts to persist entry and operation `RecoveryRequired`. If those follow-up writes also fail, the earlier durable `MutationStarted` record remains the restart-time signal that filesystem effects are uncertain.

A pre-mutation revalidation failure instead records ordinary `Failed`, because the mutation barrier was never crossed.

## Supported scope and safety

The executor rejects Move, directories, empty plans, unresolved/blocked validation and mismatched validator results. Initial Skip files never invoke the mutation primitive. Replace/overwrite remains absent.

The Windows primitive copies regular-file bytes only. It does not yet claim Explorer-style metadata, ACL, alternate-stream or extended-attribute fidelity. Directory Copy, Move and Undo remain out of scope.

The Files UI still does not instantiate or call `FileCopyOperationExecutor`; this native mutation slice remains unreachable from normal application interaction until real Windows regression tests pass and the user-visible Copy contract is completed.

## Validation without hosted Actions

Run the focused zero-Actions gate:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

The wrapper runs the existing offline suite, the executor lease-orchestration model, and the Windows mutation handle/root-binding model without consuming GitHub Actions. On a Windows machine with .NET 10, the normal local test gate additionally compiles Core/Windows and executes the real native-handle MSTest regressions.

## Next boundary

Run the real Windows/.NET regressions for `WindowsFileCopyMutationPrimitive`, then make explicit decisions about file metadata fidelity before exposing a Files Run/Execute control. No UI wiring should be merged on model-only evidence.
