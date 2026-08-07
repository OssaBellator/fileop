# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

The current implementation has four runtime layers plus a benchmark harness:

- `FileOp.Core` contains filesystem records, query parsing, index mutation/search contracts, in-memory and SQLite-backed indexes, checkpoint persistence, the fallback bounded-channel filesystem crawler and the versioned indexing-service protocol contracts.
- `FileOp.Windows` contains the Windows/NTFS engine plus the indexing-service backend/client boundary: NTFS volume discovery, MFT namespace enumeration through `FSCTL_ENUM_USN_DATA`, USN journal querying/reading, file-ID metadata hydration, multi-name hard-link snapshots, rename-safe journal normalization, transactional namespace synchronization and current-user named-pipe transport.
- `FileOp.Indexer` is the on-demand helper executable that owns native indexing and per-volume persistent index writes. It runs unelevated first and can be relaunched with UAC only when NTFS access explicitly requires it.
- `FileOp.App` is the WinUI 3 desktop shell using Windows App SDK 2.3.1. It still uses the safe crawler for its searchable snapshot until the new service boundary is reviewed and the native cutover is validated separately.
- `FileOp.Benchmarks` provides repeatable synthetic search baselines for the current SQLite-backed index at 100,000 and 1,000,000 records.

The Windows engine can read logical size, allocated size, link count, timestamps and attributes by NTFS file reference, preserve multiple namespace paths for ordinary hard-linked files, pair journal rename events, update directory subtrees without leaving stale descendant paths, reconcile targeted hard-link changes and commit namespace mutations together with the durable USN checkpoint.

`FileOp.Indexer` exposes only version negotiation, NTFS volume/status discovery, snapshot rebuild, incremental journal synchronization and search. It does not expose partition, format, cleanup or other destructive storage commands. Each NTFS volume/root pair gets its own SQLite database so rebuilding one drive cannot clear another drive's index.

The native NTFS layer intentionally does **not** replace the crawler in the UI yet. The remaining cutover work is sparse/compressed/reparse metadata semantics, measured performance validation, a filename/path search structure that can outperform the current SQLite substring scans at Everything-class scale, and a separate WinUI service-client/fallback integration slice.

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

Run the synthetic search benchmarks separately from CI:

```powershell
dotnet run -c Release --project benchmarks/FileOp.Benchmarks -- --filter *SearchBenchmarks*
```

## Repository layout

```text
src/
  FileOp.Core/       Search/index/storage domain and service protocol contracts
  FileOp.Windows/    Windows-native NTFS/USN engine and indexing IPC client/backend
  FileOp.Indexer/    On-demand native indexing helper process
  FileOp.App/        WinUI 3 desktop application
tests/
  FileOp.Windows.Tests/  NTFS and indexing-service regression/integration tests
benchmarks/
  FileOp.Benchmarks/     Synthetic persistent-index search benchmarks
docs/
  architecture.md        Architectural decisions and roadmap
  indexing-service.md    Indexing helper trust boundary and protocol model
```

## Principles

1. Speed is a feature: no feature should silently trigger a full rescan when the shared index can answer it.
2. Expensive metadata is lazy: hashes, content indexing and similar work are calculated only when requested or during idle work.
3. Destructive actions are explainable and reversible where possible.
4. The normal UI is not permanently elevated. Native indexing can use an on-demand helper with UAC only when required; future destructive storage administration remains a separate privileged surface.
5. Avoid fake optimisation features such as registry cleaning, RAM boosting and opaque health scores.
