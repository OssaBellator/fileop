# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

The current implementation has four runtime layers plus a benchmark harness:

- `FileOp.Core` contains filesystem records, query parsing, index mutation/search contracts, in-memory and SQLite-backed indexes, shared directory/file-type storage analytics, checkpoint persistence, the fallback bounded-channel filesystem crawler and the versioned indexing-service protocol contracts.
- `FileOp.Windows` contains the Windows/NTFS engine plus the indexing-service backend/client boundary: NTFS volume discovery, MFT namespace enumeration through `FSCTL_ENUM_USN_DATA`, USN journal querying/reading, file-ID metadata hydration, multi-name hard-link snapshots, rename-safe journal normalization, transactional namespace synchronization and authenticated named-pipe transport.
- `FileOp.Indexer` is the on-demand helper executable that owns native indexing and per-volume persistent index writes. It runs unelevated first; helper-only UAC elevation is limited to same-account split-token administrators so the desktop process never changes integrity level or Windows identity.
- `FileOp.App` is the WinUI 3 desktop shell using Windows App SDK 2.3.1. Search is native-first for the NTFS volume containing the user profile, and the Storage view consumes the same active metadata source for recursive folder analysis and treemap drill-down. The reviewed bounded filesystem crawler remains an explicit fallback when the helper/provider cannot be used.
- `FileOp.Benchmarks` provides repeatable synthetic search, directory-aggregation and file-type-aggregation baselines for the current SQLite-backed index at 100,000 and 1,000,000 files.

The Windows engine can read logical size, allocated size, link count, timestamps and attributes by NTFS file reference, preserve multiple namespace paths for ordinary hard-linked files, pair journal rename events, update directory subtrees without leaving stale descendant paths, reconcile targeted hard-link changes and commit namespace mutations together with the durable USN checkpoint.

`FileOp.Indexer` exposes only version negotiation, NTFS volume/status discovery, snapshot rebuild, incremental journal synchronization, search and read-only storage analysis. Protocol v3 supports both directory aggregation and file-type aggregation. It does not expose partition, format, cleanup or other destructive storage commands. Each NTFS volume/root pair gets its own SQLite database so rebuilding one drive cannot clear another drive's index. The per-session pipe is restricted to the current Windows user and to the exact desktop process ID that launched the helper.

Storage analysis consumes the same metadata rows as Search. Directory analysis returns direct entries with recursive logical bytes, allocated bytes, file count, directory count and hard-link accounting. File-type analysis groups the same indexed subtree by normalized extension, assigns a deterministic metadata-only category, and preserves the same logical-vs-physical and hard-link semantics. Whole-root totals are computed before result limits are applied. If any physical file that owns allocation lacks allocated-size metadata, physical usage remains explicitly unknown instead of substituting logical size as though it were exact.

The WinUI Storage page currently exposes directory results as summary cards, a proportional treemap and a drill-down table. In native mode it analyzes the whole indexed NTFS volume containing the user profile. In crawler fallback mode it analyzes only the already-built user-profile snapshot and states that narrower scope explicitly; it does not launch a second filesystem scan. When exact root allocation is incomplete, the treemap uses logical bytes consistently for all tiles rather than mixing logical and physical weights. If the service truncates the returned direct-entry list, the treemap derives an `Other entries` remainder from the complete root total so omitted children do not disappear from the visual accounting. The file-type engine/service contract is implemented separately so a later Types/Categories view can consume reviewed semantics without reopening the index design.

A volume is included in service-backed search or storage analysis only when it has a valid durable checkpoint and is not being maintained. If journal consistency requires a resnapshot, that checkpoint is invalidated persistently before the service reports `SnapshotRequired`.

Multiple FileOp desktop instances can create separate helper processes, so per-process semaphores are not sufficient to protect one shared volume database. Each persistent volume index therefore has a companion cross-process file gate: searches and storage analysis take shared read leases while snapshot rebuild and USN synchronization hold an exclusive maintenance lease for the entire semantic operation, including checkpoint invalidation. The lock file is a coordination primitive, not an authorization boundary.

The desktop starts `FileOp.Indexer` unelevated, builds or resumes the primary NTFS snapshot, and replays USN batches until the durable cursor converges. A low-priority background loop continues incremental catch-up without competing with foreground Search or Storage requests. If live NTFS access needs elevation, the UI can retain a valid existing native snapshot while explicitly marking it stale and offering helper-only UAC; if native indexing is unavailable entirely, FileOp builds a user-profile fallback snapshot instead.

Superseded Search and Storage interactions do not cancel an in-flight service request because an interrupted IPC exchange intentionally faults that session. The UI drops stale generations and serializes queued foreground work so only still-relevant requests reach the service. Window shutdown may cancel the active exchange because the whole helper session is being torn down.

The app build places the reviewed `FileOp.Indexer` host beside `FileOp.App`, and runtime discovery accepts only that exact adjacent non-reparse executable. This is a deterministic development/runtime location rule, not an Authenticode trust claim; signed production packaging still needs publisher/signature verification before elevation is offered.

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
- Windows 10 1809 or later for the native engine/indexer
- Windows 10 1809 or later for the desktop app
- Python 3 for the zero-Actions Storage verifiers

Build the core library and benchmark harness on any supported .NET platform:

```powershell
dotnet build src/FileOp.Core/FileOp.Core.csproj
dotnet build benchmarks/FileOp.Benchmarks/FileOp.Benchmarks.csproj
```

Build the native engine, indexing helper, run its regression tests and build the Windows app on Windows:

```powershell
dotnet build src/FileOp.Windows/FileOp.Windows.csproj
dotnet build src/FileOp.Indexer/FileOp.Indexer.csproj -p:Platform=x64
dotnet test tests/FileOp.Windows.Tests/FileOp.Windows.Tests.csproj
dotnet build src/FileOp.App/FileOp.App.csproj -p:Platform=x64
```

The desktop build copies the indexer host executable, assembly, dependency manifest and runtime configuration into the application output directory. The normal hosted workflow verifies those bundled artifacts and performs a real helper-process handshake using the copy beside the built app; the same gate can be run locally without GitHub Actions.

### Local verification without GitHub Actions

The Storage UI has a standard-library-only verifier for XAML/source wiring, treemap geometry, truncation accounting and Windows path containment:

```powershell
python tools/verify_storage_ui.py --self-test-only
python tools/verify_storage_ui.py
```

File-type analytics has a second standard-library-only verifier that executes the recursive SQLite query against synthetic fixtures and guards protocol/source wiring:

```powershell
python tools/verify_storage_types.py --self-test-only
python tools/verify_storage_types.py
```

On Windows, `tools/test-local.ps1` mirrors the hosted build/test sequence locally: both offline Storage verifiers, Core and benchmark builds, native Windows/indexer builds, regression/integration tests, WinUI x64 build, bundled-helper artifact verification and the real helper-process handshake from the app output directory.

```powershell
pwsh -File tools/test-local.ps1
```

Use `-SkipBenchmarks` for a faster development pass or `-SkipWinUI` when validating only the engine/test stack.

Run the synthetic search or storage-analysis benchmarks separately from CI:

```powershell
dotnet run -c Release --project benchmarks/FileOp.Benchmarks -- --filter *SearchBenchmarks*
dotnet run -c Release --project benchmarks/FileOp.Benchmarks -- --filter *StorageAnalyticsBenchmarks*
```

## Repository layout

```text
src/
  FileOp.Core/       Search/index/storage domain and service protocol contracts
  FileOp.Windows/    Windows-native NTFS/USN engine and indexing IPC client/backend
  FileOp.Indexer/    On-demand native indexing helper process
  FileOp.App/        WinUI 3 desktop application with shared native/fallback Search + Storage coordination
tests/
  FileOp.Windows.Tests/  NTFS, shared-index analytics and indexing-service regression/integration tests
benchmarks/
  FileOp.Benchmarks/     Synthetic persistent-index search and storage benchmarks
tools/
  verify_storage_ui.py     Zero-Actions Storage/XAML/property verifier
  verify_storage_types.py  Zero-Actions file-type SQL/protocol/source verifier
  test-local.ps1           Full local Windows build/test/handshake gate
docs/
  architecture.md        Architectural decisions and roadmap
  indexing-service.md    Indexing helper trust boundary and desktop integration model
  storage-analytics.md   Folder-size, hard-link, treemap and file-type accounting semantics
```

## Principles

1. Speed is a feature: no feature should silently trigger a full rescan when the shared index can answer it.
2. Expensive metadata is lazy: hashes, content indexing and similar work are calculated only when requested or during idle work.
3. Destructive actions are explainable and reversible where possible.
4. The normal UI is not permanently elevated. Native indexing can use an on-demand same-account helper with UAC only when safe; future destructive storage administration remains a separate privileged surface.
5. Avoid fake optimisation features such as registry cleaning, RAM boosting and opaque health scores.
