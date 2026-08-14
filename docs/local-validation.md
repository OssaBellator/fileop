# FileOp local validation without hosted Actions

FileOp keeps a complete local validation path so development/review does not depend on GitHub-hosted Actions availability or quota.

`tools/test-local.ps1` is the **authoritative verification inventory**. Individual verifier files evolve as reviewed boundaries are added; this document intentionally describes the gate by layer rather than maintaining another partial list that can drift.

## Offline/model/source gate

The offline layer requires Python 3 only. It does not require the .NET SDK or a Windows-native build.

Run it from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1 -OfflineOnly
```

That command runs all standard-library model/source verifiers currently wired into the repository gate, including Storage/Optimize, same-size content verification, physical reclaim evidence, threshold persistence, Files paging/operation/delete boundaries, recovery/history, Performance/Disk-I/O and related source/contract checks.

Most verifier files can also be run directly during focused development. Repository mode generally combines deterministic/randomized reference-model checks with committed source/XAML/protocol guards. The aggregate script remains authoritative for deciding which verifier set belongs to the current repository state.

The portable verifiers are valuable for logic, ordering and source-boundary regressions, but they do **not** prove that C#/WinUI code compiles or that Windows-native filesystem/IPC behavior succeeds.

## Complete Windows gate

On a Windows development machine with the .NET 10 SDK and Python 3:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

The full gate first runs the complete offline verifier layer, then continues through the native build/test portion. At current `main` that includes:

1. `FileOp.Core` Release build;
2. benchmark harness Release build unless `-SkipBenchmarks` is used;
3. `FileOp.Windows` Release build;
4. `FileOp.Indexer` x64 Release build;
5. Windows regression/integration tests with the built real Indexer host;
6. `FileOp.App` WinUI x64 Release build unless `-SkipWinUI` is used;
7. bundled Indexer executable/assembly/deps/runtimeconfig artifact checks;
8. a real process/pipe handshake using the helper bundled beside the built app.

`-SkipBenchmarks` and `-SkipWinUI` are development shortcuts. They are not equivalent to the complete merge gate when the skipped surface could be affected.

## What to record on a PR

When hosted Actions are unavailable or intentionally not used, the PR should state the **exact head SHA** that was validated and distinguish these categories:

- portable model/source verifier results;
- .NET test results;
- Core/Windows/Indexer/benchmark build results;
- WinUI build result;
- bundled-helper artifact result;
- real helper-process handshake result;
- any focused native filesystem/integration scenario relevant to the change.

Do not convert a successful portable model into a claim that the C#/WinUI/native gate passed. Likewise, a build alone does not replace the repository's randomized/source safety guards.

If a branch moves after validation, treat the new head as unvalidated until the relevant gate is rerun or the new commit is independently shown to be outside the validated behavior (for example, a reviewed docs-only commit).

## Batching local validation

Several source-reviewed commits can be stacked and validated together when that is more efficient. The important rules are:

- keep the final exact head identifiable;
- preserve reviewable commit boundaries where practical;
- do not merge a production/native change merely because an older head passed;
- run the complete gate before merging the final production stack;
- if a validation failure is verifier-only, confirm that the verifier assumption is actually stale before changing it rather than weakening a safety contract.

Docs-only changes can be reviewed/merged independently when they alter no source/build/test/protocol files and the documentation claims have been checked against already-merged behavior.

## Hosted Actions quota/outage handling

Do not trigger GitHub Actions merely to duplicate a local gate when hosted quota is unavailable. A review should instead record what was executed locally and what remains outstanding.

The hosted `build` workflow ignores changes confined to `docs/**` and Markdown files on both `pull_request` and `push` triggers. This prevents documentation-only reviews and merges from consuming hosted runner capacity. Any source, project, tool, test, workflow or other non-documentation change remains eligible for the normal hosted build; the path filter is a cost/queue policy, not a replacement for validation.

Commit-message workflow-skip instructions may suppress some `push`/`pull_request` workflows, but they are not validation. Skipped required checks can also remain pending under branch protection, so do not rely on skip instructions as a merge mechanism.

The repository's preferred zero-Actions path is the explicit local gate above.

## Focused native scripts

Some reviewed boundaries also have specialized local wrappers (for example focused copy/delete/native scenarios). Those are useful while developing a slice, but they supplement rather than replace `tools/test-local.ps1` as the repository-wide merge gate.
