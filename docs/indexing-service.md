# FileOp indexing service boundary

## Purpose

`FileOp.Indexer` isolates native NTFS indexing from the WinUI desktop process. The desktop application remains an ordinary `asInvoker` process. If raw NTFS volume/file-ID access requires elevation, only the indexing helper is relaunched with UAC after an explicit user action.

The indexer is deliberately not the future disk-administration helper. It exposes read/index/search operations only. Partition changes, formatting, BitLocker administration and other destructive storage operations must use a separate privileged surface with their own pre-flight and confirmation model.

## Process model

```text
FileOp.App (normal user token)
        |
        | random per-session named pipe
        | current-user-only ACL enforcement
        v
FileOp.Indexer (normal token first; elevated only when required)
        |
        +-- NTFS discovery / MFT + USN access
        +-- metadata hydration / hard-link enumeration
        +-- journal synchronization
        +-- per-volume SQLite ownership
        +-- search across volume indexes
```

The helper accepts one client, remains alive for that desktop session, and exits when the pipe disconnects. It is not installed as an always-running Windows service and it is never permanently elevated.

## IPC security

The client generates a cryptographically random pipe name for every helper launch. The server also creates the pipe with `PipeOptions.CurrentUserOnly`, so knowing or guessing a pipe name is not sufficient for another Windows user to connect.

Protocol messages use a four-byte little-endian length prefix followed by UTF-8 JSON. Frames are capped at 8 MiB before allocation. Every request includes:

- a protocol version;
- a request ID;
- an operation identifier;
- a typed JSON payload.

Every response echoes the request ID and protocol version. The client rejects mismatched IDs or protocol changes within a session.

Protocol version mismatch is rejected before native operations are dispatched.

## Version 1 operations

The initial service surface is intentionally narrow:

- `Hello` — protocol/service version and elevation state;
- `GetVolumes` — currently available NTFS volumes plus per-volume index status;
- `GetStatus` — aggregate and per-volume status;
- `RebuildVolume` — fresh MFT/metadata namespace snapshot for one volume;
- `SyncVolume` — apply one durable USN journal batch for one volume;
- `Search` — query the persistent indexes and merge results across attached NTFS volumes.

There are no file mutation, cleanup, partition, format, TRIM, BitLocker or process-management commands in this protocol.

## Error contract

Expected failures cross the process boundary as structured errors rather than raw exceptions:

- `InvalidRequest`;
- `ProtocolMismatch`;
- `VolumeNotFound`;
- `SnapshotRequired`;
- `ElevationRequired`;
- `Busy`;
- `InternalError`.

`ElevationRequired` is specifically produced when NTFS access fails with access denied. The desktop may then offer to relaunch only `FileOp.Indexer` with `runas`. Cancelling UAC leaves the desktop process unaffected.

## Per-volume persistence

Each discovered NTFS volume/root pair gets its own SQLite database. This is intentional because the current snapshot indexer clears its target index during a rebuild. A shared multi-volume database would therefore allow rebuilding one drive to erase records for another drive.

The database key includes both the NTFS serial-derived identity and the current root path. NTFS volume serials are only 32-bit and can collide across attached disks; absolute indexed paths also become stale when a volume changes drive letter. Requiring both values prevents one volume from being mistaken for another and causes a remounted volume to start with a fresh path namespace.

This is an isolation mechanism, not the final volume-identity design. A future provider may additionally persist Windows volume GUID paths or stronger filesystem-specific identities.

## Concurrency

Rebuild and journal synchronization are serialized per volume with a non-blocking operation gate. A second maintenance command for the same volume receives `Busy` rather than queueing invisibly.

Different volumes have separate contexts and failure domains. SQLite readers can continue servicing search requests while another volume is being updated. Search currently queries each attached volume index and deterministically merges the per-volume results.

## WinUI cutover policy

This service boundary is a prerequisite for switching the desktop shell to native indexing, not the cutover itself. Until the service/client lifecycle has passed review and regression testing, `FileOp.App` continues to use the reviewed bounded filesystem crawler.

The cutover should be a separate change with an explicit fallback path:

1. start an unelevated indexer session;
2. negotiate protocol version;
3. discover volumes and inspect checkpoints;
4. use persistent service-backed search when available;
5. offer helper-only elevation when the service returns `ElevationRequired`;
6. retain crawler fallback for unsupported/non-NTFS locations and service capability failures.

This keeps a service-boundary regression from silently changing file-search correctness or making the desktop process elevated.
