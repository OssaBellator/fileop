# File Copy executor orchestration

## Purpose

`FileCopyOperationExecutor` defines the execution ordering for **file Copy only** on top of canonical validation and durable action history. This slice deliberately leaves the Windows mutation primitive unimplemented: there is no production `IFileCopyMutationPrimitive`, no Files UI Run action, and no reachable target-filesystem mutation path.

A naive path-only `File.Copy(canonicalSource, canonicalDestination)` would reopen a time-of-check/time-of-use window after handle-based validation closes its inspection handles. The later Windows primitive must instead bind the mutation to the freshly validated source identity and destination namespace and exclusively create a new destination.

## Mutation lease

The primitive returns `IFileCopyMutationLease`, not a bare receipt. The lease exposes the immutable `FileCopyMutationReceipt` while retaining every kernel handle needed to keep the mutation identity-bound.

A production Windows lease must keep the source object, created destination, and every namespace handle required by its safety proof alive until the executor has:

```text
validated the receipt
        ↓
durably CommitCopy(destination identity)
        ↓
reported progress
        ↓
released the mutation lease
```

If receipt validation or durable Copy commit fails, the executor records `RecoveryRequired` **while the lease is still held**, then releases it. This prevents the safety proof from ending before the durable recovery/commit decision is made.

Lease disposal is expected to be SafeHandle-backed and no-throw. The executor treats disposal as cleanup rather than a state transition; disposal failure cannot rewrite an already durable Copy/recovery state.

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
       ↓ acquire Copy mutation lease
       ↓ read + validate identity-bound receipt
       ↓ durable CommitCopy(destination identity)
       ↓ report progress
       ↓ release mutation lease
       ↓ cancellation safe-boundary check
  ↓
durable terminal history
```

Progress for a mutated file is never reported before `CommitCopyAsync` succeeds. Progress callbacks are advisory: callback exceptions are ignored so presentation code cannot corrupt durable execution state.

## Cancellation

Cancellation uses `RequestCancellationAsync`, not an arbitrary `ExecuteAsync` token. Initial and fresh read-only validation receive an internal cancellation token.

Before `MutationStarted`, cancellation can stop immediately. Once `MutationStarted` is durable, cancellation is intentionally not passed into the mutation primitive. One file must settle to durable Commit or recovery before the request can take effect. If cancellation arrives during mutation, the current lease remains held through commit/recovery and progress, then is released before the operation settles Cancelled.

A request arriving after the final file has durably committed settles as success, matching the shared state-machine rule that fully completed work is not retroactively labelled cancelled.

## Revalidation

The validator must return the exact plan instance requested. Fresh one-file validation must remain `Ready` with a missing destination and preserve:

- canonical source-root path and stable identity;
- canonical destination-root path and stable identity;
- canonical source file path and stable identity;
- canonical destination leaf path.

Any change fails before `MutationStarted`. A collision appearing after action history began also fails conservatively rather than rewriting an already durable Pending entry into Skip.

The mutation receipt must repeat the freshly validated source/destination canonical paths and source identity, and it must provide a different stable destination identity.

## Recovery

After `MutationStarted`, primitive failure, a missing/invalid mutation lease or receipt, or durable Copy-commit failure is recovery-sensitive. The executor attempts to persist entry and operation `RecoveryRequired`. If those follow-up writes also fail, the earlier durable `MutationStarted` record remains the restart-time signal that filesystem effects are uncertain.

A pre-mutation revalidation failure instead records ordinary `Failed`, because the mutation barrier was never crossed.

## Supported scope and safety

The executor rejects Move, directories, empty plans, unresolved/blocked validation and mismatched validator results. Initial Skip files never invoke the mutation primitive. Replace/overwrite remains absent.

The executor itself is intentionally not disposable; its serialization semaphore is process-lifetime state. Only per-operation cancellation state and mutation leases are disposed, avoiding a dispose-versus-queued-execution race.

This slice contains **no concrete mutation primitive** and no `File.Copy`, `File.Move`, `File.Delete`, `Directory.Move`, `Directory.Delete`, target stream creation or other target-filesystem write implementation. The Files UI still does not instantiate or call `FileCopyOperationExecutor`.

## Validation without hosted Actions

Run the focused verifier directly:

```powershell
python tools/verify_file_copy_executor.py --repo-root . --cases 20000
```

Or run the existing offline suite first, followed by this verifier:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

The wrapper invokes `tools/test-local.ps1 -OfflineOnly` and then the Copy-executor verifier, so it consumes no GitHub Actions and requires no .NET SDK. The normal Windows local gate remains the compiler/runtime source of truth when a Windows/.NET environment is available.

## Next boundary

Implement the Windows `IFileCopyMutationPrimitive` and `IFileCopyMutationLease` with a separately reviewed handle/namespace binding strategy, exclusive destination creation and durable destination data flush before returning the lease. Do not add a Files Run control until that primitive passes real Windows integration tests. Directory Copy, Move and actual Undo remain later slices.
