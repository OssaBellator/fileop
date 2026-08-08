# FileOp indexing service boundary

## Purpose

`FileOp.Indexer` isolates native NTFS indexing from the WinUI desktop process. `FileOp.App` always remains an ordinary `asInvoker` process. If raw NTFS access requires elevation, only the on-demand indexing helper may be relaunched after explicit user action.

The indexer is not the future disk-administration helper. It exposes index/search/read-only storage-analysis operations only. Partition changes, formatting, BitLocker administration and other destructive operations require a separate privileged surface with their own pre-flight and confirmation model.

## Process model

```text
FileOp.App (normal user token + known PID)
        |
        | native-first Search/Storage coordinator
        |   \-- bounded user-profile crawler fallback
        |
        | random named pipe
        | current-user ACL + exact client-PID check
        v
FileOp.Indexer (normal token first; same-account elevation only)
        |
        +-- NTFS discovery / MFT + USN access
        +-- metadata hydration / hard-link enumeration
        +-- journal synchronization
        +-- per-volume SQLite ownership
        +-- Search + directory/type/category analytics
```

The helper accepts one authenticated client and exits when that desktop session disconnects or its launching process exits. It is not an always-running Windows service and is never permanently elevated.

## IPC security and framing

Every helper launch uses a cryptographically random pipe name. The pipe uses `PipeOptions.CurrentUserOnly`, and the server additionally verifies `GetNamedPipeClientProcessId` equals the exact desktop PID supplied at launch. An unrelated same-user process therefore cannot claim an elevated helper channel merely by discovering the pipe name.

Messages use a four-byte little-endian length prefix plus UTF-8 JSON. Frames are capped at 8 MiB before allocation. Requests carry protocol version, non-empty request ID, operation and typed payload. Responses echo protocol version and request ID. Required fields are validated after deserialization.

Oversized successful responses are replaced by retryable `ResponseTooLarge` without killing the pipe. Search and bounded Storage requests can reduce result limits and retry on the same healthy connection.

An interrupted exchange is different. If a request may have been written without its complete response being consumed, the client faults that session rather than risk request/response desynchronization. UI supersession therefore invalidates generations and discards stale work instead of cancelling transmitted IPC. Window/session shutdown may cancel because the whole helper session is being destroyed.

Protocol mismatch is rejected before backend work.

## Protocol v4

Current operations are:

- `Hello` — protocol/service version and elevation state;
- `GetVolumes` — NTFS volumes plus per-volume index status;
- `GetStatus` — aggregate/per-volume status;
- `RebuildVolume` — fresh MFT/metadata snapshot for one volume;
- `SyncVolume` — apply one durable USN batch;
- `Search` — query valid attached-volume indexes;
- `AnalyzeStorage` — recursive direct-child directory aggregation;
- `AnalyzeStorageTypes` — bounded extension rows plus exact category rollups for one indexed subtree.

Version history is deliberate:

- v1 established indexing/search;
- v2 added `AnalyzeStorage`;
- v3 added `AnalyzeStorageTypes` with bounded extension rows and complete root totals;
- v4 keeps the same `AnalyzeStorageTypes` request/operation but enriches its response with exact category rows calculated before extension truncation.

The v4 bump is required because the wire response schema changed. Desktop/helper binaries therefore fail version negotiation instead of silently disagreeing about whether exact categories are present.

There are no cleanup, file-mutation, partition, format, TRIM, BitLocker or process-management commands in this protocol.

## Storage result semantics

Both Storage operations identify `VolumeIdentity`, the current absolute volume root, and a target directory contained by that root. A durable NTFS checkpoint must be valid before either read is served.

### `AnalyzeStorage`

Returns bounded direct-entry rows with recursive logical/allocated bytes, file/directory counts, hard-link aliases and derived unique files. Whole-root totals and `DirectEntryCount` are calculated before `MaxEntries` truncation.

### `AnalyzeStorageTypes`

The request still contains:

```text
VolumeIdentity
VolumeRootPath
DirectoryPath
MaxTypes
```

The response contains complete root totals plus:

- `Types` — at most `MaxTypes` extension groups, ordered by physical allocation when root allocation is complete or logical size otherwise;
- `TypeCount` — complete distinct extension-group count before truncation;
- `Categories` — exact category aggregates over the complete extension set, independent of `MaxTypes`.

Each category row carries category identity, logical bytes, nullable allocated bytes, file count, hard-link alias count and complete extension-group `TypeCount` for that category.

Hard links preserve namespace meaning without double-counting physical space. Every visible name contributes logical bytes. Physical allocation is attributed once per stable `FileIdentity` to the case-insensitive lexicographically first path in the analysis scope. This remains true when aliases have different extensions/categories.

Allocated bytes are nullable. Unknown physical metadata remains unknown rather than being replaced with logical bytes.

Category classification is deterministic and extension-only; the helper does not inspect file contents.

## Per-volume persistence, validity and concurrency

Each NTFS volume/root pair has its own SQLite database under the current Windows user's `LocalApplicationData\FileOp\Index`. The helper chooses this location itself; an elevated process does not accept an arbitrary database path from the desktop.

Volume discovery uses the stable Windows volume GUID when available and derives the provider volume token from it; the older serial-only identity remains a fallback. Requests also carry the current root path because persisted namespace paths are absolute and a drive-letter change requires a fresh path namespace.

Every volume has two coordination layers:

- a process-local non-blocking operation gate;
- a cross-process reader/writer file gate beside the persistent database.

Search and Storage reads take shared leases. Rebuild and journal synchronization hold an exclusive maintenance lease across the entire semantic operation, including checkpoint invalidation. This prevents separate FileOp helper processes from interleaving a multi-transaction snapshot.

A volume is eligible for service-backed reads only while it has a durable checkpoint and is not being maintained. Reads acquire their gates/lease before checkpoint validation. If journal synchronization requires a fresh snapshot, the checkpoint is deleted before `SnapshotRequired` is returned so invalidity survives process restarts.

The current rebuild path temporarily removes that volume from reads. A future shadow-database build and atomic swap can eliminate that outage without exposing partial data.

## Privilege policy

The helper starts unelevated. Access denied from native NTFS operations maps to structured `ElevationRequired`.

Current helper-only UAC elevation is allowed only for a limited split administrator token, where elevation retains the same Windows user identity. Credential-over-the-shoulder elevation from a standard user is deliberately unsupported because it can launch under a different administrator SID, conflicting with the current-user pipe and per-user index path. Supporting that case requires a separately reviewed Windows service/ACL model, not weaker pipe security.

The desktop build places `FileOp.Indexer.exe`, its assembly, dependency manifest and runtime configuration beside the application. Runtime resolution accepts only the exact adjacent, non-reparse helper executable. This is a narrow path rule, not an Authenticode trust claim; signed production packaging still needs publisher/signature verification before elevation is offered.

## WinUI integration

The desktop orchestrates the NTFS volume containing the user profile first. It builds/resumes a snapshot, repeatedly calls `SyncVolume` until its durable USN cursor converges or the bounded startup budget is exhausted, then continues low-priority catch-up when foreground work is idle.

If a valid native snapshot exists but live journal access needs elevation, the snapshot remains usable and is explicitly marked non-current. If native indexing is unavailable or an invalid snapshot cannot be rebuilt, FileOp builds the bounded user-profile crawler fallback.

Search, Folders Storage and Types/Categories Storage share the same native/fallback lifecycle and foreground gates. The Storage page never opens the SQLite database directly and never launches a second crawler.

In Types mode, protocol v4 exact category rows are rendered independently of the bounded extension table. `ResponseTooLarge` retries may reduce `MaxTypes`; category semantics remain exact because the category collection is derived from the complete extension aggregate set rather than from returned top-N rows.

## Error contract

Expected errors are structured:

- `InvalidRequest`;
- `ProtocolMismatch`;
- `VolumeNotFound`;
- `SnapshotRequired`;
- `ElevationRequired`;
- `Busy`;
- `ResponseTooLarge`;
- `InternalError`.

`Busy`, `SnapshotRequired`, `ElevationRequired` and `ResponseTooLarge` are surfaced as capability/lifecycle states rather than raw implementation exceptions.

## Validation without hosted Actions

`tools/verify_storage_types.py` executes the exact protocol-v4 SQLite query with Python's standard library and verifies bounded extension rows plus exact categories, including hard links, unknown allocation and `LIMIT 1`. Repository mode checks protocol/client/dispatcher/backend/test wiring and guards SQL-source drift.

`tools/verify_storage_types_fuzz.py` compares the SQL against an independent reference model over 1,000 randomized nested-directory/hard-link/nullable-allocation fixtures.

`tools/verify_storage_ui.py` and `tools/verify_storage_ui_edgecases.py` validate the exact-category presentation and existing treemap/path invariants.

`tools/test-local.ps1` runs those offline gates before the full Core/native/indexer/tests/WinUI/bundled-helper build and real process handshake on a Windows development machine without consuming GitHub Actions usage.
