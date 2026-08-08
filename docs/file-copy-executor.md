# File Copy executor orchestration

## Purpose

FileOp now has canonical execution validation and durable per-entry action history, but those boundaries are useful only if a future mutating executor uses them in the right order. `FileCopyOperationExecutor` defines that orchestration for **file Copy only** while deliberately leaving the actual Windows mutation primitive unimplemented in this slice.

The executor implements `IFileOperationExecutor`, but it is not wired into the Files UI and no production `IFileCopyMutationPrimitive` exists yet. Therefore this branch still exposes no reachable target-filesystem mutation path.

## Why the mutation primitive is separate

A naive `File.Copy(canonicalSource, canonicalDestination)` would still have a time-of-check/time-of-use window after handle-based canonical validation closes its inspection handles. Another actor could replace a source object or namespace component between validation and the path-based copy.

`IFileCopyMutationPrimitive` is therefore a deliberately strong boundary. A later Windows implementation must bind the mutation to the freshly validated source identity and destination location, create a **new** destination without overwrite, and return a `FileCopyMutationReceipt` containing the source/destination canonical paths and stable identities actually used by the mutation.

The orchestrator rejects a receipt if its source identity/path differs from the fresh validation or if the destination identity equals the source identity. Implementing the primitive is a separate review slice so the Win32 handle-binding design can be reviewed independently of execution-state orchestration.

## Execution order

For a supported Copy-file plan, execution is:

```text
Planned
  ↓
Validating
  ↓ full canonical validation
  ↓ durable Begin action history
Running
  ↓
for each entry
  ├─ initial Skip → report completed, no mutation
  └─ initial Ready
       ↓ fresh one-entry canonical validation
       ↓ compare root/source identity + canonical paths with initial validation
       ↓ cancellation safe-boundary check
       ↓ durable MarkMutationStarted
       ↓ exactly one CopyNewFileAsync primitive call
       ↓ validate mutation receipt
       ↓ durable CommitCopy(destination identity)
       ↓ report progress
       ↓ cancellation safe-boundary check
  ↓
durable terminal history
  ↓
Succeeded / Cancelled / Failed
```

Progress for a mutated file is never reported before `CommitCopyAsync` succeeds.

## Cancellation boundary

`ExecuteAsync` intentionally has no arbitrary cancellation token. Cancellation uses the reviewed `RequestCancellationAsync` path.

Before `MutationStarted`, cancellation can stop immediately because no target mutation has begun. The canonical validator receives an internal cancellation token so read-only initial/per-entry validation can be interrupted.

Once `MutationStarted` has been durably recorded, cancellation is **not** passed into the mutation primitive. One file must settle to a safe boundary first. If cancellation arrives during the durable barrier or primitive call, the executor completes/records that one file (or marks it recovery-required) before stopping.

A cancellation arriving after the final entry committed resolves as success, matching the shared execution-state rule that fully completed work is not retroactively labelled cancelled.

## Revalidation changes

The executor begins action history from one immutable full validation result. A ready file is then revalidated again immediately before its mutation boundary.

The fresh result must still be `Ready` with a missing destination and must match:

- the original canonical source-root path and identity;
- the original canonical destination-root path and identity;
- the file's canonical source path and source identity;
- the canonical destination leaf path.

If any of those change, the entry fails **before** `MutationStarted` and no mutation primitive is called.

A destination that appears after history began is also treated as a pre-mutation failure, even when collision policy is `Skip`. The executor does not rewrite the already-durable `Pending` decision into a new Skip decision mid-operation. A future design could add a dedicated durable revalidation-transition if dynamic Skip behavior is valuable; the first mutating boundary stays conservative.

## Recovery behavior

After `MutationStarted`, any primitive exception, invalid mutation receipt, or `CommitCopyAsync` failure is recovery-sensitive.

The executor attempts to persist `RecoveryRequired` on the entry and terminal operation. If those follow-up writes fail too, the earlier durable `MutationStarted` record remains the restart-time signal that the filesystem effect is uncertain.

A pre-mutation failure instead records `Failed` and can terminate normally as Failed because the mutation barrier was never crossed.

## Supported scope

`FileCopyOperationExecutor` rejects:

- Move plans;
- any plan containing a directory entry;
- empty plans;
- initial validation that is blocked or still needs a collision decision;
- per-entry validation or mutation receipts that no longer match the immutable/canonical identity boundary.

Initial `Skip` file entries are counted as completed without invoking the mutation primitive. Replace/overwrite is still absent.

## Safety boundary

This slice contains **no concrete mutation primitive**. In particular, the new Core executor contains no `File.Copy`, `File.Move`, `File.Delete`, `Directory.Move`, `Directory.Delete`, target stream creation, or other filesystem-write implementation.

The Files UI still contains no Run/Execute action and does not instantiate or call `FileCopyOperationExecutor`.

## Validation without hosted Actions

The focused standard-library verifier is:

```powershell
python tools/verify_file_copy_executor.py --repo-root . --cases 20000
```

It models ordering, revalidation changes, safe-boundary cancellation, post-barrier failure/recovery, initial Skip behavior and the rule that progress follows durable commit.

The whole offline gate remains:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

The normal Windows local gate additionally compiles the Core executor and runs its fake-validator/history/mutation MSTest regressions without consuming GitHub Actions usage.

## Next boundary

Implement the Windows `IFileCopyMutationPrimitive` with an explicitly reviewed handle/identity-binding strategy and exclusive destination creation. Do not wire Run into Files until that primitive passes real Windows integration tests and the full local gate. Directory Copy, Move and actual Undo remain later slices.
