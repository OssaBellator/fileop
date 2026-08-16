# PR #185 reconstruction WIP — 2026-08-16

This file is intentionally on `wip/pr185-hardening-20260816`, not on PR #185.
It records source-reconstruction decisions only. It is **not** compiler/test evidence and must not be used to mark PR #185 ready or merge it.

## Durable refs at reconstruction start

- current `main`: `81b14bab74d55ebf972066228602b7a0a2228565`
- PR #185 remote head: `ce22c456ead55012313170ddaa480922ee66ec73`
- PR #185 state at race-check: open, draft, unmerged, mergeable=false
- old PR base: `8007b3f4d78b03481c8ee488aaa7891229b7db26`

The final candidate must be reconstructed from current `main` plus the GitHub PR head. No crashed Windows workspace is authoritative.

## PR/main overlap files

Do not overwrite current-main behavior wholesale in these 10 files:

- `docs/files-browser.md`
- `src/FileOp.App/FilesView.Move.cs`
- `src/FileOp.App/FilesView.xaml`
- `src/FileOp.Windows/Operations/WindowsFileOperationVolumeRelationship.cs`
- `src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs`
- `tests/FileOp.Windows.Tests/WindowsFileOperationVolumeRelationshipTests.cs`
- `tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs`
- `tools/test-local.ps1`
- `tools/verify_files_same_volume_move_ui.py`
- `tools/verify_move_volume_identity.py`

The other 45 PR-changed files may be transplanted from `ce22c456...` onto current `main` before applying the hardening below.

## Required hardening to reconstruct

### WinUI compile/rebase

`src/FileOp.App/FilesView.Move.cs`

- add `using FileOp.Core.Models;`
- retain current-main `UpdateDirectoryMoveExecutionAvailability`
- retain current-main Directory Move wording/executor and shared busy-state behavior
- merge only the dormant cross-volume UI plumbing from #185

`src/FileOp.App/FilesView.xaml`

- retain current-main same-volume Directory Move control/layout/status
- layer dormant cross-volume controls/text without replacing Directory Move behavior

### SQLite durability

`src/FileOp.Core/Operations/SqliteFileCrossVolumeMoveActionHistoryStore.cs`

SQLite `synchronous` is connection-scoped. Require `FULL` both during initialization and on every opened connection.

Initialization must include:

```sql
PRAGMA foreign_keys = ON;
PRAGMA synchronous = FULL;
PRAGMA journal_mode = WAL;
```

Every `OpenConnection()` must execute the equivalent of:

```sql
PRAGMA foreign_keys = ON;
PRAGMA synchronous = FULL;
PRAGMA busy_timeout = 5000;
```

Add a behavioral regression that opens/reopens the store and queries effective `PRAGMA synchronous`, requiring numeric mode `2` (`FULL`).

### Global multi-entry chronology/frontier invariant

`FileCrossVolumeMoveActionHistory` must validate global entry order during construction, after per-entry validation and before terminal-state validation.

Semantics:

- `Skipped` is neutral anywhere.
- `Moved` forms the completed executable prefix, ignoring skips.
- At most one non-skipped active/terminal frontier may exist among `CopyMutationStarted`, `DestinationCommitted`, `SourceDeleteStarted`, `Failed`, `RecoveryRequired`.
- After the first `Pending` or active frontier, every later non-skipped entry must remain `Pending`.
- reject a later `Moved`.
- reject a later destructive/terminal frontier.
- reject a second active frontier.

Required regressions:

- Pending then later source-delete frontier rejected.
- Moved after Pending rejected.
- second active frontier rejected.
- Skipped entries neutral around chronology.
- SQLite mutation of a later entry out of order throws and transaction rolls back.
- persisted hydration where ordinal 1 is destructive while ordinal 0 remains Pending throws.

### Production volume identity / kill-switch

Preserve current-main `WindowsFileOperationVolumeRelationship.cs` unchanged unless new evidence requires otherwise.

`WindowsMoveOperationExecutionValidator` behavior:

- different serials => immediately return unified cross-volume product block; do not probe GUID/namespace/history/mutation.
- equal serial + different stable volume GUID => same unified cross-volume product block.
- equal serial + same GUID => continue same-volume namespace checks.
- stronger proof unavailable => fail closed.

Use one `CrossVolumeMoveDisabledSummary` rather than stale per-case summary names. Rebase affected tests/verifiers.

### NTFS/POSIX source-delete capability boundary

The dormant direct/native engine must remain behind the production kill-switch and must not broaden filesystem support.

- compose direct/native execution through the repository's existing NTFS mutation capability guard.
- before `SourceDeleteStarted`, positively prove the exact source volume supports `FILE_SUPPORTS_POSIX_UNLINK_RENAME`.
- bind the capability proof to the exact already identity-bound source volume/handle.
- failure/unavailable capability evidence must fail before the destructive durable barrier.
- native runner/docs/inventory must require supported mutation filesystems; do not merely require two local writable volumes.
- add deterministic tests for the POSIX capability bit; unrelated filesystem flags must not satisfy it.

### Checked source-delete close receipt

The raw POSIX disposition path must prove completion of the exact source delete handle's close boundary before durable `Moved`.

- add an explicit checked one-shot close operation/receipt on the source-delete lease.
- the executor must require this close receipt after `SourceDeleteMutationPerformed` and before `CommitSourceDeletedAsync`.
- keep destination/source identity evidence held until checked close completes.
- if checked close fails after `SourceDeleteStarted`, route through recovery-sensitive settlement, normally `RecoveryRequired`.
- cleanup after a checked-close attempt may remain best effort and must not be treated as proof.
- add deterministic fake/provider close-failure regression.
- raw Windows implementation should use a checked native close for the exact owned source delete handle rather than relying on `SafeHandle.Dispose()` to surface failure.

### Compatibility/rebase fixes

- replace stale MSTest `Assert.ThrowsException` with current `Assert.ThrowsExactly` where appropriate.
- stale metadata name `PreservedStableAttributes` => current `StableCopiedAttributes`.
- `MergeDestinationAttributes` tests must use current argument order `(destinationAttributes, sourceAttributes)`.
- if test binaries contradict fixed source, `dotnet clean` before diagnosis.

### Verifier/doc rebases

Rebase rather than suppress:

- `tools/verify_file_cross_volume_move.py`
- `tools/verify_move_volume_identity.py`
- `tools/verify_files_same_volume_move_ui.py`
- `tools/verify_cross_volume_move_native_inventory.py`
- `tools/verify_cross_volume_move_source_preflight_native_inventory.py`
- `tools/verify_directory_same_volume_move.py`
- current-main recycle verifier must remain in `tools/test-local.ps1`
- preserve current-main wording/contracts needed by `tools/verify_file_operation_state.py`

`verify_file_cross_volume_move.py` must model checked-close success as a prerequisite for durable `Moved`; source-string checks alone are insufficient.

## Prior unpushed Windows evidence — useful, never merge-authorizing

On a fresh Windows Tester reconstruction before connector loss:

- Windows: `Windows-10-10.0.26200-SP0`
- .NET SDK: `10.0.302`
- identity: `TIANDI\\unkno`
- `TokenElevationType=Limited`
- `TokenIsElevated=false`
- administrator=false
- only filesystem volume: writable NTFS `C:\\`, serial `BC2B340E`
- source volume reported POSIX unlink/rename support
- focused Windows sets passed 35/35 and 20/20
- ordinary-token security gate passed 1/1
- all 82 offline commands extracted from `tools/test-local.ps1` passed at their configured case counts
- WinUI/Core/Windows/benchmark/Indexer Release builds observed green; WinUI final build reported 0 warnings / 0 errors

The aggregate `tools/test-local.ps1` invocation and monolithic Windows test run were inconclusive because the Windows Tester connector timed out/terminated. The mandatory two-volume native gate was impossible because only `C:` existed. None of the working-tree fixes above were committed or pushed.

## Required final gate

Only after a real candidate is committed and pushed to PR #185, rerun on the **exact pushed PR SHA** under an ordinary unelevated Windows token:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-security.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-native.ps1 -SourceRoot <volA> -DestinationRoot <volB>
```

The native roots must be two distinct writable filesystem volumes with different stable volume serials. Two directories on one volume are never a substitute.

Record exact SHA, Windows/.NET versions, token elevation state, roots, volume serials, complete outputs, and every inconclusive test.

If no suitable second volume exists, keep #185 draft/unmerged and continue hardening only. Mark ready and squash-merge to `main` only after every required exact-head Windows gate passes.
