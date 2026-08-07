# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

The current implementation has three runtime layers plus a benchmark harness:

- `FileOp.Core` contains filesystem records, query parsing, index mutation/search contracts, in-memory and SQLite-backed indexes, checkpoint persistence and the fallback bounded-channel filesystem crawler.
- `FileOp.Windows` contains the Windows/NTFS engine: NTFS volume discovery, MFT namespace enumeration through `FSCTL_ENUM_USN_DATA`, USN journal querying/reading, file-ID metadata hydration, multi-name hard-link snapshots, rename-safe journal normalization and transactional namespace synchronization.
- `FileOp.App` is the WinUI 3 desktop shell using Windows App SDK 2.3.1. It still uses the safe crawler for its searchable snapshot while the native indexing service boundary and performance/search-index work are completed and validated.
- `FileOp.Benchmarks` provides repeatable synthetic search baselines for the current SQLite-backed index at 100,000 and 1,000,000 records.

The Windows engine can read logical size, allocated size, link count, timestamps and attributes by NTFS file reference, preserve multiple namespace paths for ordinary hard-linked files, pair journal rename events, update directory subtrees without leaving stale descendant paths, reconcile targeted hard-link changes and commit namespace mutations together with the durable USN checkpoint.

Hard-link enumeration is only invoked when NTFS metadata reports more than one link, so the common single-link snapshot path remains the fast MFT/file-ID path. A mismatch between the reported link count and enumerated namespace paths is treated as a consistency failure and requests a fresh snapshot instead of persisting incomplete data.

The native NTFS layer intentionally does **not** replace the crawler in the UI yet. The remaining cutover work is primarily the indexing service/privilege boundary, sparse/compressed/reparse metadata semantics, measured performance validation and a filename/path search structure that can outperform the current SQLite substring scans at Everything-class scale.

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
- Windows 10 1809 or later for the desktop app

Build the core library and benchmark harness on any supported .NET platform:

```powershell
dotnet build src/FileOp.Core/FileOp.Core.csproj
dotnet build benchmarks/FileOp.Benchmarks/FileOp.Benchmarks.csproj
```

Build the native engine, run its regression tests and build the Windows app on Windows:

```powershell
dotnet build src/FileOp.Windows/FileOp.Windows.csproj
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
  FileOp.Core/       Search/index/storage domain and cross-platform services
  FileOp.Windows/    Windows-native NTFS/USN engine
  FileOp.App/        WinUI 3 desktop application
tests/
  FileOp.Windows.Tests/  NTFS journal/persistence/native regression tests
benchmarks/
  FileOp.Benchmarks/     Synthetic persistent-index search benchmarks
docs/
  architecture.md    Architectural decisions and roadmap
```

## Principles

1. Speed is a feature: no feature should silently trigger a full rescan when the shared index can answer it.
2. Expensive metadata is lazy: hashes, content indexing and similar work are calculated only when requested or during idle work.
3. Destructive actions are explainable and reversible where possible.
4. The normal UI is not permanently elevated. Future privileged storage operations will run through a narrow helper process and UAC only when required.
5. Avoid fake optimisation features such as registry cleaning, RAM boosting and opaque health scores.
