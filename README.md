# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

The current implementation has three layers:

- `FileOp.Core` contains filesystem records, query parsing, index mutation/search contracts, in-memory and SQLite-backed indexes, checkpoint persistence and the fallback bounded-channel filesystem crawler.
- `FileOp.Windows` contains the Windows/NTFS engine: NTFS volume discovery, MFT namespace enumeration through `FSCTL_ENUM_USN_DATA`, USN journal querying/reading, record parsing, file-reference hierarchy reconstruction, file-ID metadata hydration, rename-safe journal normalization and transactional namespace synchronization.
- `FileOp.App` is the WinUI 3 desktop shell using Windows App SDK 2.3.1. It still uses the safe crawler for its searchable snapshot while the native namespace model and privilege/service boundary are completed and validated.

The Windows engine can now read logical size, allocated size, link count, timestamps and attributes by NTFS file reference, pair journal rename events, update directory subtrees without leaving stale descendant paths, and commit index mutations together with the durable USN checkpoint.

The native NTFS layer intentionally does **not** replace the crawler in the UI yet. The current snapshot path resolver still represents one namespace path per file reference, so it is not a complete hard-link enumerator. The next engine work is a complete namespace snapshot model, an indexing service/privilege boundary, and performance benchmarks before switching the app's default indexer.

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

Build the core library on any supported .NET platform:

```powershell
dotnet build src/FileOp.Core/FileOp.Core.csproj
```

Build the native engine, run its regression tests and build the Windows app on Windows:

```powershell
dotnet build src/FileOp.Windows/FileOp.Windows.csproj
dotnet test tests/FileOp.Windows.Tests/FileOp.Windows.Tests.csproj
dotnet build src/FileOp.App/FileOp.App.csproj -p:Platform=x64
```

## Repository layout

```text
src/
  FileOp.Core/       Search/index/storage domain and cross-platform services
  FileOp.Windows/    Windows-native NTFS/USN engine
  FileOp.App/        WinUI 3 desktop application
tests/
  FileOp.Windows.Tests/  NTFS journal/persistence regression tests
docs/
  architecture.md    Architectural decisions and roadmap
```

## Principles

1. Speed is a feature: no feature should silently trigger a full rescan when the shared index can answer it.
2. Expensive metadata is lazy: hashes, content indexing and similar work are calculated only when requested or during idle work.
3. Destructive actions are explainable and reversible where possible.
4. The normal UI is not permanently elevated. Future privileged storage operations will run through a narrow helper process and UAC only when required.
5. Avoid fake optimisation features such as registry cleaning, RAM boosting and opaque health scores.
