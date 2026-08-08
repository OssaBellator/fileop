# File Copy executor orchestration

## Purpose

`FileCopyOperationExecutor` defines the execution ordering for **file Copy only** on top of canonical validation and durable action history. This slice deliberately leaves the Windows mutation primitive unimplemented: there is no production `IFileCopyMutationPrimitive`, no Files UI Run action, and no reachable target-filesystem mutation path.

A naive `File.Copy(canonicalSource, canonicalDestination)` would reopen a time-of-check/time-of-use window after the handle-based validator closes its inspection handles. The later Windows primitive must therefore bind Copy to the freshly validated source identity and destination location, exclusively create a new destination, and return the exact canonical paths and stable identities it used.

## Ordering

```text
full canonical validation
  ↓
durable Begin action history
  ↓
for each entry
  ├─ initial Skip → progress only
  └─ initial Ready
       ↓ fresh one-entry canonical validation
       ↓ verify same root paths/identities, source path/identity, destination path
       ↓ cancellation check
       ↓ durable MarkMutationStarted
       ↓ one CopyNewFileAsync primitive call
       ↓ validate mutation receipt
       ↓ durable CommitCopy(destination identity)
       ↓ progress
       ↓ cancellation safe-boundary check
  ↓
durable terminal history
```

Progress for a mutated file is never reported before `CommitCopyAsync` succeeds.

## Cancellation and recovery

Cancellation uses `RequestCancellationAsync`, not an arbitrary `ExecuteAsync` token. Read-only validation is internally cancellable. Before `MutationStarted`, cancellation can stop immediately. Once `MutationStarted` is durable, cancellation is not passed into the primitive: that one file must settle to Commit or recovery before cancellation takes effect. A late request after the final committed entry settles as success.

The validator must return the exact plan instance it was asked to validate. Fresh per-file validation must remain `Ready` with a missing destination and preserve the original canonical root identities, source identity/path and destination canonical path. A changed identity/path or a new collision is a pre-mutation failure.

After `MutationStarted`, primitive failure, an invalid receipt, or durable Copy-commit failure is recovery-sensitive. The executor attempts to mark the entry and operation `RecoveryRequired`; if those follow-up writes fail, the earlier durable `MutationStarted` record remains the restart-time signal.

## Supported scope and safety

The executor rejects Move, directories, empty plans, unresolved/blocked validation and mismatched validation/receipt data. Initial Skip files never invoke the mutation primitive. Replace/overwrite remains absent.

This slice contains **no concrete mutation primitive** and no `File.Copy`, `File.Move`, `File.Delete`, `Directory.Move`, `Directory.Delete`, target stream creation or other target-filesystem write implementation. The Files UI still does not instantiate or call `FileCopyOperationExecutor`.

`FileCopyOperationExecutor` is intentionally not disposable. It owns a process-lifetime serialization semaphore and creates/disposes only per-operation cancellation state. This avoids a dispose-versus-queued-execution race around the semaphore while preserving one active Copy operation at a time.

## Validation without hosted Actions

Run the focused verifier directly:

```powershell
python tools/verify_file_copy_executor.py --repo-root . --cases 20000
```

Or run the existing offline suite first, followed by this verifier:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

The wrapper invokes `tools/test-local.ps1 -OfflineOnly` and then the Copy executor verifier, so it does not consume GitHub Actions or require the .NET SDK. The normal Windows local gate remains the compiler/runtime test when a Windows/.NET environment is available.

## Next boundary

Implement the Windows `IFileCopyMutationPrimitive` with a separately reviewed handle/identity-binding strategy and exclusive destination creation. Do not wire Run into Files until that primitive passes real Windows integration tests. Directory Copy, Move and actual Undo remain later slices.
