# FileOp local validation without hosted Actions

FileOp keeps a local/offline validation path so development and review do not depend on GitHub-hosted Actions availability or quota.

## Fast standard-library checks

These checks require Python 3 only and do not require Windows or the .NET SDK.

```powershell
python tools/verify_storage_ui.py --self-test-only
python tools/verify_storage_types.py --self-test-only
```

From a repository checkout, omit `--self-test-only` to also validate the committed source wiring:

```powershell
python tools/verify_storage_ui.py --repo-root .
python tools/verify_storage_types.py --repo-root .
```

The Storage UI verifier checks XAML/event wiring, required controls, Windows path containment, treemap bounds/non-overlap/proportionality and complete-root accounting when returned direct entries are truncated.

The Storage file-type verifier executes the same recursive SQLite query used by `SqliteStorageAnalytics` against deterministic fixtures and checks nested recursion, normalized extensions, cross-extension hard-link allocation, incomplete allocation, result truncation, sibling-root exclusion and empty roots. Repository mode also checks protocol-v3/client/dispatcher/backend wiring, regression-test presence, duplicate classifier switch patterns and exact SQL-source parity.

## Full Windows gate

On a Windows development machine with the .NET 10 SDK and Python 3:

```powershell
pwsh -File tools/test-local.ps1
```

That command mirrors the hosted build/test sequence locally:

1. Storage UI/property verifier.
2. Storage file-type SQL/source verifier.
3. `FileOp.Core` Release build.
4. Benchmark harness Release build.
5. `FileOp.Windows` Release build.
6. `FileOp.Indexer` x64 Release build.
7. Windows regression/integration tests using the real indexer host.
8. `FileOp.App` WinUI x64 Release build.
9. Bundled helper artifact verification.
10. Real process/pipe handshake using the helper copied beside the built app.

`-SkipBenchmarks` shortens iterative development runs. `-SkipWinUI` validates the engine/test stack when a WinUI build is not needed.

## Opening a review PR while Actions quota is unavailable

GitHub supports commit-message skip instructions for workflows triggered by `push` and `pull_request`. When hosted Actions must not run, the review branch HEAD commit may include `[skip actions]` before the pull request is opened.

This suppresses the hosted workflow; it does **not** make the change validated. The PR description must record which offline/local checks were actually executed and which Windows compiler/runtime checks remain outstanding.

GitHub may leave skipped required checks in a pending state. If repository protection later requires those checks, a normal non-skip HEAD commit and executable CI will still be necessary before merge.

Do not use skip instructions for routine convenience when hosted validation is available. They are a quota/outage review mechanism, not a replacement for the normal build gate.
