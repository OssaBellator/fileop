# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

The current implementation has four runtime layers plus a benchmark harness:

- `FileOp.Core` contains filesystem records, query parsing, index mutation/search contracts, exact paged directory-browse models, in-memory and SQLite-backed indexes, shared directory/file-type/category storage analytics, read-only storage-optimization analysis, aggregate storage-history persistence/deltas, checkpoint persistence, the fallback crawler and versioned service contracts.
- `FileOp.Windows` contains the Windows/NTFS engine and indexing-service boundary: NTFS discovery, MFT namespace enumeration, USN journal processing, file-ID metadata hydration, hard-link expansion, transactional namespace synchronization, authenticated named-pipe transport and read-only browse/history/optimization service composition.
- `FileOp.Indexer` is the on-demand helper that owns native indexing and per-volume persistent-index writes. It starts unelevated; helper-only UAC is limited to same-account split-token administrators so the desktop never changes integrity level or identity.
- `FileOp.App` is the WinUI 3 desktop shell. Search, Files and Storage share the same native-first metadata source and explicit crawler fallback; Files consumes exact paged direct-child browsing, while native Storage exposes Folders, Types, History and a read-only Optimize advisor.
- `FileOp.Benchmarks` provides synthetic search, directory-aggregation and file-type/category baselines at 100,000 and 1,000,000 files.

The NTFS engine can hydrate logical/allocated size, link count, timestamps and attributes by file ID, preserve multiple hard-link namespace paths, pair rename events, move directory subtrees transactionally, reconcile hard-link changes and commit namespace mutations together with durable USN checkpoints.

`FileOp.Indexer` exposes version negotiation, volume/status discovery, snapshot rebuild, incremental journal synchronization, search, exact paged direct-child browsing, read-only Storage analytics, read-only optimization analysis and aggregate Storage-history capture/query. **Protocol v7** adds `AnalyzeStorageOptimization`; the v6 `BrowseDirectory` keyset-paging contract and v5 history operations remain intact. The protocol exposes no partition, format, cleanup or other destructive commands.

Each NTFS volume/root pair has its own SQLite database. Live reads require a valid durable checkpoint and use shared cross-process leases; rebuild/sync operations use an exclusive maintenance lease so multiple FileOp instances cannot interleave one logical snapshot.

## Search, Files and Storage

Search uses the persistent native NTFS index when available and falls back to the bounded user-profile crawler when required. Startup builds/resumes the primary NTFS snapshot, catches the USN cursor up in bounded batches and continues low-priority synchronization behind foreground work.

Files and Storage consume the same metadata rows as Search. Neither performs another recursive filesystem scan merely to browse indexed names or calculate storage breakdowns.

### Files

Files is a read-only exact paged browser. It requests 256 direct entries at a time through `DesktopSearchEngine.BrowseDirectoryAsync`, exposes `Load more` only while the protocol returns a continuation cursor and appends pages in service order.

`BrowseDirectory` returns direct `FileRecord` metadata rather than recursive Storage aggregates. Native paging uses the persisted parent identity when available, the existing cross-process read lease and a valid durable checkpoint. Ordering is stable within each live page sequence: directories first, then normalized name and normalized path. Continuation is keyset-based rather than offset-based, so inserts before an existing cursor do not shift already-consumed rows into later pages.

The desktop coordinator exposes the same paged model for fallback mode by paging the already-completed in-memory profile snapshot. That path does not touch the filesystem again; native mode reads SQLite directly and does not materialize the whole persistent index.

Files rows show direct metadata only: name, type, logical/allocated size for files and last-write time. Directory recursive sizes remain a Storage responsibility. The page sequence is live rather than transactionally frozen; the UI distinguishes a completed sequence from a count mismatch caused by directory changes while pages were read.

### Folders

Directory analysis returns direct entries with recursive logical bytes, nullable physical allocation, file/directory counts and hard-link accounting. The WinUI Storage page provides summary cards, drill-down, a detailed table and a proportional treemap. When direct results are truncated, the treemap derives an exact `Other entries` remainder from complete root totals.

### Types and Categories

File-type analysis groups the same subtree by normalized extension and deterministic metadata-only category. Every visible hard-link name contributes logical namespace bytes; physical allocation is counted once per stable `FileIdentity`.

`MaxTypes` bounds only the extension table. Root totals and complete `TypeCount` are calculated before truncation, while exact category rows are aggregated over the complete extension set. Category bars therefore remain exact even when the UI shows only the top N extensions.

### Growth history

Aggregate history stores root totals plus exact category rollups in the same per-volume database; it does **not** retain per-file history or trigger another filesystem scan. Namespace rebuilds do not erase prior observations, and root identity uses ordinal-ignore-case semantics rather than SQLite's ASCII-only `NOCASE`.

Protocol v5 introduced trusted native history and those operations remain part of v7:

- `CaptureStorageHistory` has no client timestamp. The service first materializes a valid exact-category live analysis, then writes it into the current UTC-hour bucket. Repeated captures in one hour are idempotent replacements.
- `GetStorageHistory` returns a bounded persisted series. It requires the physical volume/root to remain attached but does not require the current namespace checkpoint to still be valid.

The desktop schedules **whole-primary-volume native observations opportunistically**. Scheduling belongs to `DesktopSearchEngine`, so it does not depend on the Storage page being opened. A capture is attempted only when the native index is current, yields briefly after synchronization, and takes both foreground/native gates with non-blocking acquisition. Existing Search/Files/Storage work therefore wins; contention defers history instead of queuing it ahead of the user. Once a named-pipe capture request has been transmitted it is allowed to complete, preserving the existing IPC synchronization rule.

Within one desktop session, a successful UTC-hour bucket suppresses further automatic captures until the next hour. Server-side bucket idempotency also prevents duplicate rows across retries or app restarts.

History is native-only and intentionally tracks the whole primary indexed volume rather than every folder the user happens to browse. Its timeline displays physical allocation only when every displayed observation has exact allocation; otherwise the entire timeline uses logical size. “What grew?” compares the newest two observations using `StorageHistoryDelta` and shows signed category changes. It does not present forecasting or claim a long-term trend from two points.

Fallback-history semantics remain deliberately undefined so a static crawler snapshot cannot silently be mixed into the native durable series.

### Optimize

The native **Optimize** view is a read-only evidence advisor over the same durable index. It currently surfaces:

- large files, ranked by physical allocation when known and logical size otherwise;
- old large files using an explicit last-write age threshold;
- same-size groups of distinct physical files as a cheap duplicate-candidate prefilter.

Hard-link aliases are collapsed by stable file identity before candidates are ranked or grouped. Same-size groups are **not confirmed duplicates**: equal length does not establish content equality. Their displayed potential savings is a logical upper bound until a future lazy content-verification stage confirms matching bytes.

The initial Optimize view is native-only. FileOp does not present the profile-scoped fallback crawler snapshot as complete reclaim analysis. The advisor performs no delete, cleanup, cache clearing, registry modification or opaque health scoring; destructive cleanup remains a separately authorized boundary. See `docs/storage-optimization.md` for policy and safety semantics.

Storage therefore has **Folders, Types, History and Optimize** modes: Folders explains where space is used, Types explains what uses it, History explains what changed, and Optimize identifies evidence-backed items worth reviewing.

## Trust and privilege boundary

The desktop process always remains non-elevated. Native indexing starts with the current token and reports structured `ElevationRequired` when needed. Same-account helper-only UAC is allowed only for split-token administrators; different-admin credential elevation remains unsupported until a separately reviewed service/ACL design exists.

The per-session pipe is restricted to the current Windows user and exact desktop PID. Requests are versioned, framed and capped at 8 MiB. Oversized responses return retryable `ResponseTooLarge` without destroying an otherwise healthy session.

The app build places the reviewed `FileOp.Indexer` host beside `FileOp.App`. Runtime resolution accepts only that exact adjacent non-reparse executable. This is a deterministic location rule, not an Authenticode trust claim; signed packaging still needs publisher/signature verification before elevation is offered.

## Query examples

```text
invoice
ext:pdf invoice
size:>100mb
ext:zip size:>1gb backup
```

## Build

Requirements:

- .NET 10 SDK
- Windows 10 1809 or later for native engine/indexer and desktop app
- Python 3 for the zero-Actions verifiers

Core/benchmarks:

```powershell
dotnet build src/FileOp.Core/FileOp.Core.csproj
dotnet build benchmarks/FileOp.Benchmarks/FileOp.Benchmarks.csproj
```

Windows stack:

```powershell
dotnet build src/FileOp.Windows/FileOp.Windows.csproj
dotnet build src/FileOp.Indexer/FileOp.Indexer.csproj -p:Platform=x64
dotnet test tests/FileOp.Windows.Tests/FileOp.Windows.Tests.csproj
dotnet build src/FileOp.App/FileOp.App.csproj -p:Platform=x64
```

The desktop build copies the indexer host executable, assembly, dependency manifest and runtime configuration beside the app. The full local gate verifies those artifacts and performs a real helper-process handshake using the bundled copy.

## Local verification without GitHub Actions

Standard-library-only checks include:

```powershell
python tools/verify_storage_ui.py --self-test-only
python tools/verify_storage_types.py --self-test-only
python tools/verify_storage_types_fuzz.py --cases 1000
python tools/verify_storage_optimization.py --cases 10000
python tools/verify_storage_history.py --self-test-only --cases 1000
python tools/verify_storage_history_unicode.py --self-test-only
python tools/verify_storage_history_service.py --self-test-only --cases 2000
python tools/verify_storage_history_ui.py --self-test-only --cases 10000
python tools/verify_files_ui.py --self-test-only --cases 10000
python tools/verify_directory_browse.py --self-test-only --cases 10000
```

Repository-mode variants validate source wiring as well. The Optimize verifier checks hard-link collapse, measured-size ranking, stale-age filtering, same-size upper-bound semantics, read-only service wiring, WinUI disclaimers and stale-load invalidation. The Files verifier checks exact page accumulation, Load-more/cursor lifecycle and active-load supersession; the paged-browse verifier separately exercises SQLite keyset pagination over randomized fixtures and guards read-only database access, cross-process lease/checkpoint wiring, native/fallback routing and the no-rescan rule.

Run all standard-library verifiers without requiring the .NET SDK:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

On Windows, run the complete no-Actions gate:

```powershell
pwsh -File tools/test-local.ps1
```

It runs the offline verifiers, Core/benchmark builds, native Windows/indexer builds, regression/integration tests, WinUI x64 build, bundled-helper checks and real process handshake. Use `-SkipBenchmarks` or `-SkipWinUI` for narrower development passes.

Benchmarks remain manual:

```powershell
dotnet run -c Release --project benchmarks/FileOp.Benchmarks -- --filter *SearchBenchmarks*
dotnet run -c Release --project benchmarks/FileOp.Benchmarks -- --filter *StorageAnalyticsBenchmarks*
```

## Repository layout

```text
src/
  FileOp.Core/       Search/index/browse/storage/history/optimization domain and service contracts
  FileOp.Windows/    Windows-native NTFS/USN engine and indexing IPC client/backends
  FileOp.Indexer/    On-demand native indexing helper process
  FileOp.App/        WinUI 3 desktop app with Search + exact paged Files + Storage

tests/
  FileOp.Windows.Tests/  NTFS, browsing, analytics/history/optimization and service regression/integration tests

benchmarks/
  FileOp.Benchmarks/     Synthetic persistent-index Search/Storage benchmarks

tools/
  verify_storage_ui.py              Storage/XAML/property verifier
  verify_storage_ui_edgecases.py    Targeted Storage UI regressions
  verify_storage_types.py           Exact file-type/category SQL/source verifier
  verify_storage_types_fuzz.py      Randomized SQL/reference parity verifier
  verify_storage_optimization.py    Read-only optimization model/source/UI verifier
  verify_storage_history.py         Aggregate history SQLite/delta verifier
  verify_storage_history_unicode.py Ordinal-ignore-case history-root verifier
  verify_storage_history_service.py Protocol-v7 history capture/query verifier
  verify_storage_history_ui.py      Native history scheduler/WinUI verifier
  verify_files_ui.py                Exact paged Files UI/source verifier
  verify_directory_browse.py        Protocol-v7 keyset browse verifier
  test-local.ps1                    Offline-only or full local Windows gate

docs/
  architecture.md          Architectural decisions and roadmap
  indexing-service.md      Helper trust boundary and protocol model
  files-browser.md         Exact indexed Files UI and paging semantics
  storage-analytics.md     Folder/type/category accounting semantics
  storage-history.md       Aggregate history persistence/service/delta semantics
  storage-optimization.md  Read-only optimization policy and safety semantics
  storage-types-ui.md      WinUI Types/Categories lifecycle and presentation
```

## Principles

1. Speed is a feature: no feature should silently trigger a full rescan when the shared index can answer it.
2. Expensive metadata is lazy: hashes, content indexing and similar work are calculated only when requested or during idle work.
3. Destructive actions are explainable and reversible where possible.
4. The normal UI is never permanently elevated; future destructive administration remains a separate privileged surface.
5. Avoid fake optimisation features such as registry cleaning, RAM boosting and opaque health scores.
