# PR #185 main-based reconstruction progress — 2026-08-16

This file lives only on `wip/pr185-reconstruct-main-20260816`. It records source integration work while Windows Tester is unavailable. **Nothing here is Windows compile/test evidence and this branch must not be used to mark PR #185 ready or merge it.**

## Durable source state

- current `main`: `81b14bab74d55ebf972066228602b7a0a2228565`
- current PR #185 head: `ce22c456ead55012313170ddaa480922ee66ec73`
- PR #185 remains open/draft/unmerged/mergeable=false
- clean transplant commit: `9d0faa35684b2f7daf26c45d28402cfa4ed923b3`
  - parent is exact current `main`
  - exactly the 45 conflict-free PR files were transplanted byte-for-byte by PR-head blob SHA
  - none of the nominal 10 overlap files were replaced
- WIP validator merge: `93c381edad7c72ec6d929dccf5e6c5945fdd35ee`
- paired PR validator regression blob: `8064906e68566a0b191e0f963a28eae9e20aa383`
- merged randomized volume-identity verifier: `c6aae732227ec9e479a5a54e5f28c1c9c93c7acb`

The current WIP branch is source-review material only and has not been compiled.

## Overlap reduction

The original 10 nominal overlap paths reduce to eight files requiring an actual content merge:

- `tests/FileOp.Windows.Tests/WindowsFileOperationVolumeRelationshipTests.cs` is byte-identical on current main and PR head (`d02e10dcc86929903d75f46a5dc40ef29d9a8e13`), so no merge is required.
- `src/FileOp.Windows/Operations/WindowsFileOperationVolumeRelationship.cs` differs, but current-main implementation is deliberately retained unchanged because it is the stronger identity-bound volume-GUID proof.

Eight real content merges remain:

1. `docs/files-browser.md`
2. `src/FileOp.App/FilesView.Move.cs`
3. `src/FileOp.App/FilesView.xaml`
4. `src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs` — WIP source merge already applied on this branch
5. `tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs` — WIP PR regression blob already paired on this branch
6. `tools/test-local.ps1`
7. `tools/verify_files_same_volume_move_ui.py`
8. `tools/verify_move_volume_identity.py` — WIP merged randomized verifier already applied on this branch

## Validator merge now present on WIP branch

The WIP `WindowsMoveOperationExecutionValidator` keeps current-main constructor compatibility while using the reviewed product-boundary behavior:

- mutation-ready Move without both exact root identities blocks before probes;
- different root volume serials immediately return `CrossVolumeMoveDisabledSummary` and do **not** query the stronger GUID relationship or namespace capabilities;
- equal serials query the exact identity-bound `WindowsFileOperationVolumeRelationshipProbe`;
- equal serial + same GUID may continue to same-volume namespace checks;
- equal serial + different GUID returns the same `CrossVolumeMoveDisabledSummary`;
- unavailable stronger proof fails closed;
- product block remains before durable history/mutation.

This intentionally keeps `src/FileOp.Windows/Operations/WindowsFileOperationVolumeRelationship.cs` at current-main bytes.

The WIP volume-identity verifier keeps current-main randomized modelling and product-composition checks, but models different serials as `BLOCKED` rather than stale `READY` and requires the unified product block before GUID/namespace probing. It also retains the schema-v1 distinct-serial journal constraint.

## Remaining UI overlap merge

### `FilesView.Move.cs`

Do not transplant the PR file wholesale. Current main has live homogeneous Directory Move support in a separate executor and its shared busy-state plumbing must remain.

Required merge:

- add `using FileOp.Core.Models;` (the PR cross-volume branch uses `FileIdentity`; absence caused a real WinUI compile failure in the earlier disposable reconstruction);
- retain current-main file-vs-directory routing and `UpdateDirectoryMoveExecutionAvailability()` calls;
- retain `RunQueuedDirectoryMoveButton.IsEnabled = false` in shared Move busy state;
- retain file executor refusal text that directs homogeneous directory-only plans to the separate Directory Move executor;
- layer the dormant cross-volume file-history/progress/outcome plumbing from #185 without making it production reachable;
- product `WindowsMoveOperationExecutionValidator` must still block different-volume Move before the UI can invoke the cross-volume executor;
- any dormant direct composition must be NTFS-wrapped before it is used in test-only/direct contexts.

The old PR patch changes cancellation/progress/outcome formatting and adds `FileCrossVolumeMoveActionHistory` handling. Those pieces may be merged, but current-main Directory Move controls must not be removed or relabelled as unsupported.

### `FilesView.xaml`

Keep current-main layout and controls:

- `RunQueuedMoveButton` remains **Run File Move**;
- `RunQueuedDirectoryMoveButton` remains present with its loaded handler and **Run Directory Move** label;
- the shared Cancel Move button remains positioned after both file/directory Run buttons;
- current-main text continues to advertise reviewed same-volume local file **and homogeneous directory** Move;
- cross-volume remains product-disabled/dormant;
- do not reintroduce the stale PR text claiming directory Move is disabled.

### `tools/verify_files_same_volume_move_ui.py`

Keep current-main randomized model for separate file and directory Move execution. Add only dormant cross-volume file assertions:

- product cross-volume classification remains non-executable;
- source file may contain the dormant cross-volume journal/formatter/composite types;
- current-main Directory Move source, guard, tests and XAML assertions stay mandatory;
- keep the no-path-based `File.Move`/`Directory.Move`/`File.Copy`/`File.Delete` checks.

Earlier disposable Windows reconstruction reached `250,097` model checks with the merged file/directory UI model; that is useful historical evidence only and must be rerun after reconstruction.

## `tools/test-local.ps1`

Start from current main. Preserve every current-main gate, especially the recycle transaction verifier. Insert the two PR additions after the same-volume file Move executor verifier:

```powershell
Invoke-Step "Offline cross-volume file Move composite verifier" {
    python tools/verify_file_cross_volume_move.py --repo-root $repoRoot --cases 50000
}

Invoke-Step "Offline cross-volume Move source-preflight verifier" {
    python tools/verify_cross_volume_move_source_preflight.py --repo-root $repoRoot --cases 50000
}
```

Do not replace the current-main script with the stale PR version.

## Constructor-level cross-volume chronology/frontier invariant

Exact insertion point in `FileCrossVolumeMoveActionHistory`:

```csharp
for (var ordinal = 0; ordinal < snapshot.Length; ordinal++)
{
    ValidateEntry(...);
}

ValidateEntryChronology(snapshot);
ValidateTerminalState(snapshot, terminalState, completedAtUtc);
```

Required validator semantics, ignoring `Skipped` entries:

```csharp
private static void ValidateEntryChronology(
    IReadOnlyList<FileCrossVolumeMoveActionEntry> entries)
{
    var frontierReached = false;
    foreach (var entry in entries)
    {
        if (entry.State == FileCrossVolumeMoveEntryState.Skipped)
        {
            continue;
        }

        if (!frontierReached && entry.State == FileCrossVolumeMoveEntryState.Moved)
        {
            continue;
        }

        if (!frontierReached &&
            entry.State is FileCrossVolumeMoveEntryState.Pending or
                FileCrossVolumeMoveEntryState.CopyMutationStarted or
                FileCrossVolumeMoveEntryState.DestinationCommitted or
                FileCrossVolumeMoveEntryState.SourceDeleteStarted or
                FileCrossVolumeMoveEntryState.Failed or
                FileCrossVolumeMoveEntryState.RecoveryRequired)
        {
            frontierReached = true;
            continue;
        }

        if (frontierReached && entry.State == FileCrossVolumeMoveEntryState.Pending)
        {
            continue;
        }

        throw new ArgumentException(
            "Cross-volume Move history must preserve a completed Moved prefix followed by at most one active/terminal frontier and then only Pending entries; Skipped entries are neutral.");
    }
}
```

This produces the required invariants:

- later destructive/terminal frontier after Pending is rejected;
- Moved after Pending/frontier is rejected;
- second active/terminal frontier is rejected;
- Skipped remains neutral anywhere.

The exact production implementation may split the cases for clearer diagnostics, but it must preserve these semantics and run during object construction so `LoadHistory()` throws before a SQLite transaction commits.

Required tests:

- `PendingThenLaterSourceDeleteFrontierIsRejected`
- `MovedAfterPendingIsRejected`
- `SecondActiveFrontierIsRejected`
- `SkippedEntriesAreNeutralAroundExecutableChronology`
- a store mutation that attempts a later frontier while an earlier entry remains Pending must throw and roll back
- persisted hydration with ordinal 0 Pending and ordinal 1 destructive must throw

Also rebase old `Assert.ThrowsException<T>` calls to current MSTest `Assert.ThrowsExactly<T>` where applicable.

## SQLite FULL durability insertion points

Current PR-head store initialization begins:

```sql
PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
```

Change it to:

```sql
PRAGMA foreign_keys = ON;
PRAGMA synchronous = FULL;
PRAGMA journal_mode = WAL;
```

Current `OpenConnection()` executes:

```sql
PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;
```

Change it to the equivalent of:

```sql
PRAGMA foreign_keys = ON;
PRAGMA synchronous = FULL;
PRAGMA busy_timeout = 5000;
```

Add a behavioral regression that opens/reopens the store, queries `PRAGMA synchronous`, and requires numeric mode `2` (`FULL`). A source-string verifier is supplementary, not sufficient.

## NTFS-only direct/native composition

Current main already defines `WindowsNtfsMutationExecutionValidator`, which binds freshly validated source/destination root identities to exact handle-observed NTFS evidence. The current mutation `FileIdentity` model is intentionally NTFS-only.

The PR native two-volume test currently bypasses the production product block with:

```csharp
new WindowsFileOperationExecutionValidator()
```

both in `CreateRealExecutor` and `AssertReadyOnDifferentVolumesAsync`, and also in direct composite constructions such as the cancellation-after-copy case.

The direct/native cross-volume engine should bypass **only** the production cross-volume product kill-switch, not the NTFS mutation boundary. Compose it through the existing NTFS decorator, e.g. the equivalent of:

```csharp
new WindowsNtfsMutationExecutionValidator(
    new WindowsFileOperationExecutionValidator())
```

and update all direct/native test compositions and inventory checks consistently. Do not broaden ReFS/other filesystem support just to make the matrix run.

## Exact source-volume POSIX unlink capability

The raw source-delete provider currently opens and identity-validates `sourceDirectory`, then later uses `FILE_DISPOSITION_POSIX_SEMANTICS` without proving the exact source volume advertises that capability.

Proof must occur before the lease is returned and therefore before durable `SourceDeleteStarted`.

Use the already-opened, identity-validated `sourceDirectory` handle with `GetVolumeInformationByHandleW`. Capture `lpFileSystemFlags` and require:

```csharp
private const uint FileSupportsPosixUnlinkRename = 0x00000400u;
```

(or the verified Windows SDK value used by the implementation).

The helper must fail closed if the handle-bound filesystem name is not NTFS or if `(flags & FileSupportsPosixUnlinkRename) == 0`. It must not infer capability from a drive letter or a separate path lookup.

Add deterministic tests for the capability classifier: the POSIX bit passes; unrelated filesystem bits do not.

## Checked exact source-delete handle close

Current raw lease behavior is insufficient:

1. `NtSetInformationFile(FileDispositionInformationEx)` succeeds;
2. `SourceDeleteMutationPerformed` becomes true;
3. `DisposeAsync()` invokes `DisposeNoThrow(_sourceFile)`;
4. Core then commits durable `Moved` if cleanup returns.

This does not provide a positive checked receipt that the exact POSIX delete handle reached its close boundary.

Required contract change:

- add a distinct one-shot checked-close operation/receipt to `IFileCrossVolumeMoveSourceDeleteLease`;
- it must be separate from cleanup `DisposeAsync()`;
- the fidelity wrapper must delegate the receipt without treating wrapper disposal as proof;
- Core must require: disposition performed -> checked close succeeds/receipt true -> cleanup/release -> durable `CommitSourceDeletedAsync`;
- if checked close fails after `SourceDeleteStarted`, route through the existing `FailSourceDeleteBarrierAsync` path, normally `RecoveryRequired`;
- destination/source evidence handles stay live until the checked source-file close completes;
- after a checked-close attempt, later cleanup may be best effort.

The raw Windows lease should own the exact source handle and perform a checked, one-shot native close (the earlier disposable reconstruction used the NT close boundary rather than trusting `SafeHandle.Dispose()` to surface release failure). Prevent the normal `SafeFileHandle` finalizer/dispose path from closing that same raw handle a second time after ownership is detached for the checked close.

Add a deterministic fake/provider regression equivalent to `CheckedSourceDeleteHandleCloseFailureAfterBarrierRequiresRecovery` and require that durable `Moved` is impossible without the positive close receipt.

## Known stale compatibility fixes

Reapply during reconstruction:

- MSTest `Assert.ThrowsException` -> `Assert.ThrowsExactly` where current MSTest requires it;
- `PreservedStableAttributes` -> `StableCopiedAttributes`;
- `MergeDestinationAttributes` test calls use `(destinationAttributes, sourceAttributes)`;
- if binaries contradict source, run `dotnet clean` before diagnosis.

## Current blocking condition

Windows Tester is not presently exposed by the connector/plugin catalog. Therefore this WIP branch must not be promoted to PR #185 and no compile/test claims may be made from it.

When Windows Tester returns:

1. clone/fetch current GitHub `main`, PR head, and this WIP branch;
2. race-check refs again;
3. finish the remaining overlaps/hardening above;
4. run native Windows focused tests/builds/verifiers;
5. complete the aggregate local and ordinary-token security gates;
6. inspect real filesystem volumes;
7. if and only if two distinct writable volumes with different stable serials exist, run the native matrix;
8. commit/push a candidate to PR #185;
9. rerun all three required commands against that **exact pushed SHA**;
10. only then, if all gates pass, mark ready and squash-merge.

Two directories on one volume are never a substitute for the final native gate.
