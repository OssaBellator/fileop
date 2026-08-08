# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

The current implementation has four runtime layers plus a benchmark harness:

- `FileOp.Core` contains filesystem records, query parsing, index mutation/search contracts, in-memory and SQLite-backed indexes, shared directory/file-type/category storage analytics, aggregate storage-history persistence/deltas, checkpoint persistence, the fallback crawler and versioned service contracts.
- `FileOp.Windows` contains the Windows/NTFS engine and indexing-service boundary: NTFS discovery, MFT namespace enumeration, USN journal processing, file-ID metadata hydration, hard-link expansion, transactional namespace synchronization and authenticated named-pipe transport.
- `FileOp.Indexer` is the on-demand helper that owns native indexing and per-volume persistent-index writes. It starts unelevated; helper-only UAC is limited to same-account split-token administrators so the desktop never changes integrity level or identity.
- `FileOp.App` is the WinUI 3 desktop shell. Search and Storage share the same native-first metadata source and the same explicit crawler fallback.
- `FileOp.Benchmarks` provides synthetic search, directory-aggregation and file-type/category baselines at 100,000 and 1,000,000 files.

The NTFS engine can hydrate logical/allocated size, link count, timestamps and attributes by file ID, preserve multiple hard-link namespace paths, pair rename events, move directory subtrees transactionally, reconcile hard-link changes and commit namespace mutations together with durable USN checkpoints.

`FileOp.Indexer` exposes only version negotiation, volume/status discovery, snapshot rebuild, incremental journal synchronization, search and read-only storage analytics. **Protocol v4** supports directory analysis and `AnalyzeStorageTypes`, whose response contains bounded extension rows plus exact category rollups. It exposes no partition, format, cleanup or other destructive commands.

Each NTFS volume/root pair has its own SQLite database. Reads require a valid durable checkpoint and use shared cross-process leases; rebuild/sync operations use an exclusive maintenance lease so multiple FileOp instances cannot interleave one logical snapshot.

## Search and Storage

Search uses the persistent native NTFS index when available and falls back to the bounded user-profile crawler when required. Startup builds/resumes the primary NTFS snapshot, catches the USN cursor up in bounded batches and continues low-priority synchronization behind foreground work.

Storage consumes the same metadata rows as Search. It never performs another recursive scan merely to calculate sizes or breakdowns.

### Folders

Directory analysis returns direct entries with recursive logical bytes, nullable physical allocation, file/directory counts and hard-link accounting. The WinUI Storage page provides summary cards, drill-down, a detailed table and a proportional treemap. When direct results are truncated, the treemap derives an exact `Other entries` remainder from complete root totals.

### Types and Categories

File-type analysis groups the same subtree by normalized extension and deterministic metadata-only category. Every visible hard-link name contributes logical namespace bytes; physical allocation is counted once per stable `FileIdentity`.

`MaxTypes` bounds only the extension table. Root totals and complete `TypeCount` are calculated before truncation. Protocol v4 additionally returns **exact category rows over the complete extension set**, so category bars remain exact even when the UI shows only the top N extensions.

Category rows report logical bytes, nullable physical allocation, file count, hard-link aliases and the complete number of extension groups in that category. If root physical allocation is incomplete, the UI uses logical weighting consistently rather than mixing units. A category may legitimately have zero physical bytes when all of its names are non-canonical hard-link aliases.

The current categories are No Extension, Documents, Images, Video, Audio, Archives, Applications, Code, Data, Disk Images, Fonts and Other. Classification is extension-based only; FileOp does not inspect content to guess MIME types.

### Growth history

`FileOp.Core` now has an aggregate history store for Storage observations. A snapshot persists root logical/physical totals plus the exact category rollups already produced by file-type analysis; it does **not** retain per-file history or trigger another filesystem scan.

History lives in the same per-volume SQLite database as the current index, under an independently versioned `storage_history_*` sub-schema. Namespace rebuilds clear current file/checkpoint state without erasing prior observations. Same-root/same-timestamp writes are idempotent, retention pruning cascades category rows, and signed deltas preserve unknown physical usage whenever either observation is incomplete.

This slice is persistence/domain only. Capture scheduling and the user-facing growth timeline must be integrated through the indexing-service validity/lease boundary so historical observations are taken only from a trustworthy indexed snapshot.

## Trust and privilege boundary

The desktop process always remains non-elevated. Native indexing starts with the current token and reports structured `ElevationRequired` when needed. Same-account helper-only UAC is allowed only for split-token administrators; different-admin credential elevation is deliberately unsupported until a separately reviewed service/ACL design exists.

The per-session pipe is restricted to the current Windows user and to the exact desktop PID that launched the helper. Requests are versioned, framed and capped at 8 MiB. Oversized responses return retryable `ResponseTooLarge` without destroying an otherwise healthy session.

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
- Windows 10 1809 or later for the native engine/indexer and desktop app
- Python 3 for the zero-Actions Storage verifiers

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

Standard-library-only Storage checks:

```powershell
python tools/verify_storage_ui.py --self-test-only
python tools/verify_storage_ui.py
python tools/verify_storage_ui_edgecases.py
python tools/verify_storage_types.py --self-test-only
python tools/verify_storage_types.py
python tools/verify_storage_types_fuzz.py --cases 1000
python tools/verify_storage_history.py --self-test-only --cases 1000
python tools/verify_storage_history.py --cases 1000
```

The file-type verifier executes the exact SQLite query from `SqliteStorageAnalytics`, including protocol-v4 bounded type rows and exact category aggregates. The randomized verifier compares that SQL against an independent hard-link/nullable-allocation reference model. The history verifier exercises idempotent snapshots, rebuild survival, cascade pruning and randomized signed-delta reconciliation.

On Windows, run the complete no-Actions gate:

```powershell
pwsh -File tools/test-local.ps1
```

It runs the offline verifiers, Core/benchmark builds, native Windows/indexer builds, regression/integration tests, WinUI x64 build, bundled-helper checks and the real process/pipe handshake. Use `-SkipBenchmarks` or `-SkipWinUI` for narrower development passes.

Benchmarks remain manual:

```powershell
dotnet run -c Release --project benchmarks/FileOp.Benchmarks -- --filter *SearchBenchmarks*
dotnet run -c Release --project benchmarks/FileOp.Benchmarks -- --filter *StorageAnalyticsBenchmarks*
```

## Repository layout

```text
src/
  FileOp.Core/       Search/index/storage/history domain and service protocol contracts
  FileOp.Windows/    Windows-native NTFS/USN engine and indexing IPC client/backend
  FileOp.Indexer/    On-demand native indexing helper process
  FileOp.App/        WinUI 3 desktop app with shared native/fallback Search + Storage

tests/
  FileOp.Windows.Tests/  NTFS, shared-index analytics/history and service regression/integration tests

benchmarks/
  FileOp.Benchmarks/     Synthetic persistent-index Search/Storage benchmarks

tools/
  verify_storage_ui.py           Storage/XAML/property verifier
  verify_storage_ui_edgecases.py Targeted Storage UI regressions
  verify_storage_types.py        Exact file-type/category SQL/source verifier
  verify_storage_types_fuzz.py   Randomized SQL/reference parity verifier
  verify_storage_history.py      Aggregate history SQLite/delta verifier
  test-local.ps1                 Full local Windows build/test/handshake gate

docs/
  architecture.md        Architectural decisions and roadmap
  indexing-service.md    Helper trust boundary and protocol model
  storage-analytics.md   Folder/type/category accounting semantics
  storage-history.md     Aggregate Storage history persistence and delta semantics
  storage-types-ui.md    WinUI Types/Categories lifecycle and presentation
```

## Principles

1. Speed is a feature: no feature should silently trigger a full rescan when the shared index can answer it.
2. Expensive metadata is lazy: hashes, content indexing and similar work are calculated only when requested or during idle work.
3. Destructive actions are explainable and reversible where possible.
4. The normal UI is never permanently elevated; future destructive administration remains a separate privileged surface.
5. Avoid fake optimisation features such as registry cleaning, RAM boosting and opaque health scores.
