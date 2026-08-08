# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

The current implementation has four runtime layers plus a benchmark harness:

- `FileOp.Core` contains filesystem records, query parsing, index mutation/search contracts, in-memory and SQLite-backed indexes, shared storage-analytics contracts/aggregation, checkpoint persistence, the fallback bounded-channel filesystem crawler and the versioned indexing-service protocol contracts.
- `FileOp.Windows` contains the Windows/NTFS engine plus the indexing-service backend/client boundary: NTFS volume discovery, MFT namespace enumeration through `FSCTL_ENUM_USN_DATA`, USN journal querying/reading, file-ID metadata hydration, multi-name hard-link snapshots, rename-safe journal normalization, transactional namespace synchronization and authenticated named-pipe transport.
- `FileOp.Indexer` is the on-demand helper executable that owns native indexing and per-volume persistent index writes. It runs unelevated first; helper-only UAC elevation is limited to same-account split-token administrators so the desktop process never changes integrity level or Windows identity.
- `FileOp.App` is the WinUI 3 desktop shell using Windows App SDK 2.3.1. Search is now native-first for the NTFS volume containing the user profile, with the reviewed bounded filesystem crawler retained as an explicit fallback when the helper/provider cannot be used.
- `FileOp.Benchmarks` provides repeatable synthetic search and directory-aggregation baselines for the current SQLite-backed index at 100,000 and 1,000,000 files.

The Windows engine can read logical size, allocated size, link count, timestamps and attributes by NTFS file reference, preserve multiple namespace paths for ordinary hard-linked files, pair journal rename events, update directory subtrees without leaving stale descendant paths, reconcile targeted hard-link changes and commit namespace mutations together with the durable USN checkpoint.

`FileOp.Indexer` exposes only version negotiation, NTFS volume/status discovery, snapshot rebuild, incremental journal synchronization, search and read-only storage analysis. It does not expose partition, format, cleanup or other destructive storage commands. Each NTFS volume/root pair gets its own SQLite database so rebuilding one drive cannot clear another drive's index. The per-session pipe is restricted to the current Windows user and to the exact desktop process ID that launched the helper.

Storage analysis now consumes the same metadata rows as Search. A directory request returns its direct entries ranked by treemap size, with each directory recursively aggregated into logical bytes, allocated bytes, file count and directory count. Whole-root totals are computed before the result limit is applied. If any file in an aggregate lacks allocated-size metadata, physical usage remains explicitly unknown instead of substituting logical size as though it were exact. The crawler-backed in-memory index implements the same result contract for the future fallback UI path.

A volume is included in service-backed search or storage analysis only when it has a valid durable checkpoint and is not being maintained. If journal consistency requires a resnapshot, that checkpoint is invalidated persistently before the service reports `SnapshotRequired`.

Multiple FileOp desktop instances can create separate helper processes, so per-process semaphores are not sufficient to protect one shared volume database. Each persistent volume index therefore has a companion cross-process file gate: searches and storage analysis take shared read leases while snapshot rebuild and USN synchronization hold an exclusive maintenance lease for the entire semantic operation, including checkpoint invalidation. The lock file is a coordination primitive, not an authorization boundary.

The desktop starts `FileOp.Indexer` unelevated, builds or resumes the primary NTFS snapshot, and replays USN batches until the durable cursor converges. A low-priority background loop continues incremental catch-up without competing with active searches. If live NTFS access needs elevation, the UI can retain a valid existing native snapshot while explicitly marking it stale and offering helper-only UAC; if native indexing is unavailable entirely, FileOp builds a user-profile fallback snapshot instead.

Superseded searches do not cancel an in-flight service request because an interrupted IPC exchange intentionally faults that session. The UI drops stale generations and serializes queued searches so only the newest pending query reaches the service. Window shutdown may cancel the active exchange because the whole helper session is being torn down.

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

The desktop build copies the indexer host executable, assembly, dependency manifest and runtime configuration into the application output directory. CI verifies those bundled artifacts and performs a real helper-process handshake using the copy beside the built app.

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
  FileOp.App/        WinUI 3 desktop application and native/fallback search coordinator
tests/
  FileOp.Windows.Tests/  NTFS, shared-index analytics and indexing-service regression/integration tests
benchmarks/
  FileOp.Benchmarks/     Synthetic persistent-index search and storage benchmarks
docs/
  architecture.md        Architectural decisions and roadmap
  indexing-service.md    Indexing helper trust boundary and desktop integration model
```

## Principles

1. Speed is a feature: no feature should silently trigger a full rescan when the shared index can answer it.
2. Expensive metadata is lazy: hashes, content indexing and similar work are calculated only when requested or during idle work.
3. Destructive actions are explainable and reversible where possible.
4. The normal UI is not permanently elevated. Native indexing can use an on-demand same-account helper with UAC only when safe; future destructive storage administration remains a separate privileged surface.
5. Avoid fake optimisation features such as registry cleaning, RAM boosting and opaque health scores.
