# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

The current implementation has two engine layers:

- `FileOp.Core` contains filesystem records, query parsing, index mutation/search contracts, in-memory and SQLite-backed indexes, checkpoint persistence and the fallback bounded-channel filesystem crawler.
- `FileOp.Windows` contains the Windows/NTFS-specific engine foundation: NTFS volume discovery, MFT namespace enumeration through `FSCTL_ENUM_USN_DATA`, USN journal querying/reading, record parsing and file-reference hierarchy reconstruction.
- `FileOp.App` is the WinUI 3 desktop shell using Windows App SDK 2.3.1. It still uses the safe crawler for its searchable snapshot while the native metadata pipeline is completed and validated.

File records can now carry stable NTFS-style `(volume serial, file reference)` identities, and `IFileIndex` supports upsert/delete change batches so journal events can update an index instead of forcing a rescan.

The native NTFS layer intentionally does **not** replace the crawler yet. `FSCTL_ENUM_USN_DATA` provides fast namespace/file-reference information but is not sufficient by itself for complete logical size, allocated size, all hard-link names and other metadata FileOp needs. The next engine work is targeted/native metadata hydration plus journal-to-index processing before switching the app's default indexer.

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

Build the Windows app and native engine on Windows:

```powershell
dotnet build src/FileOp.App/FileOp.App.csproj -p:Platform=x64
```

## Repository layout

```text
src/
  FileOp.Core/       Search/index/storage domain and cross-platform services
  FileOp.Windows/    Windows-native NTFS/USN engine
  FileOp.App/        WinUI 3 desktop application
docs/
  architecture.md    Architectural decisions and roadmap
```

## Principles

1. Speed is a feature: no feature should silently trigger a full rescan when the shared index can answer it.
2. Expensive metadata is lazy: hashes, content indexing and similar work are calculated only when requested or during idle work.
3. Destructive actions are explainable and reversible where possible.
4. The normal UI is not permanently elevated. Future privileged storage operations will run through a narrow helper process and UAC only when required.
5. Avoid fake optimisation features such as registry cleaning, RAM boosting and opaque health scores.
