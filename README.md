# FileOp

FileOp is a high-performance Windows file and storage manager built around one reusable filesystem index.

The product direction is to combine instant file search, power-user file management, storage analysis, duplicate detection, storage cleanup, disk health and—later—carefully isolated administrative operations such as partition management.

## Status

This repository contains the first implementation slice:

- a .NET 10 core library with filesystem records, query parsing and an in-memory search index;
- a bounded-channel filesystem crawler that indexes without blocking the UI thread;
- a WinUI 3 desktop shell using Windows App SDK 2.3.1;
- a storage overview using `DriveInfo`;
- a debounced search experience with basic `ext:` and `size:` filters;
- architecture boundaries for a future NTFS MFT/USN-backed index provider;
- Windows and cross-platform build checks in GitHub Actions.

The current crawler is intentionally a bootstrap provider, not the final performance engine. The next major milestone is a Windows-specific NTFS provider that reads filesystem metadata efficiently and tails the USN journal while keeping the same `IFileIndex` contract.

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

Build the Windows app on Windows:

```powershell
dotnet build src/FileOp.App/FileOp.App.csproj -p:Platform=x64
```

## Repository layout

```text
src/
  FileOp.Core/       Search/index/storage domain and services
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
