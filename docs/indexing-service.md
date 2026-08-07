# FileOp indexing service boundary

## Purpose

`FileOp.Indexer` isolates native NTFS indexing from the WinUI desktop process. The desktop application remains an ordinary `asInvoker` process. If raw NTFS volume/file-ID access requires elevation, only the indexing helper may be relaunched with UAC after an explicit user action.

The indexer is deliberately not the future disk-administration helper. It exposes read/index/search operations only. Partition changes, formatting, BitLocker administration and other destructive storage operations must use a separate privileged surface with their own pre-flight and confirmation model.

## Process model

```text
FileOp.App (normal user token + known PID)
        |
        | native-first search coordinator
        |   \-- bounded user-profile crawler fallback
        |
        | random per-session named pipe
        | current-user-only ACL + exact client-PID check
        v
FileOp.Indexer (normal token first; same-account elevation only when supported)
        |
        +-- NTFS discovery / MFT + USN access
        +-- metadata hydration / hard-link enumeration
        +-- journal synchronization
        +-- per-volume SQLite ownership
        +-- search across valid volume indexes
```

The helper accepts one authenticated client, remains alive for that desktop session, and exits when the pipe disconnects or the launching desktop process exits. It is not installed as an always-running Windows service and it is never permanently elevated.

## IPC security

The client generates a cryptographically random pipe name for every helper launch. The server creates the pipe with `PipeOptions.CurrentUserOnly`, so another Windows user cannot connect.

`CurrentUserOnly` alone is not treated as sufficient for an elevated helper: an unrelated process running under the same user account might otherwise race the desktop for a discovered pipe name. The launcher therefore passes its own process ID to the helper, and after every pipe connection the server calls Windows `GetNamedPipeClientProcessId`. It accepts the channel only when the connected process ID exactly matches the live desktop process that launched it; unrelated same-user clients are disconnected before any protocol request is read.

Protocol messages use a four-byte little-endian length prefix followed by UTF-8 JSON. Frames are capped at 8 MiB before allocation. Every request includes:

- a protocol version;
- a non-empty request ID;
- an operation identifier;
- a typed JSON payload.

Every response echoes the request ID and protocol version. The client rejects mismatched IDs or protocol changes within a session. Required payload fields are validated explicitly after JSON deserialization; a positional record with missing JSON properties is not accepted merely because the serializer can construct it with default values.

If any response would exceed the 8 MiB frame cap, the server substitutes a small retryable `ResponseTooLarge` error rather than terminating the session. For search results, the desktop reduces the result limit and retries on the same connection.

A cancelled or otherwise interrupted client exchange is different: once a request may have been written without its complete matching response being consumed, the client faults and closes that connection instead of risking request/response desynchronization. A subsequent operation must use a fresh indexer session.

The WinUI coordinator therefore does not use query supersession as a cancellation signal. A new keystroke invalidates the visible generation immediately; queued stale searches are discarded before transmission and completed stale searches are discarded before rendering. Only session/window shutdown deliberately interrupts the active exchange because the whole helper session is being destroyed.

Protocol version mismatch is rejected before native operations are dispatched.

## Version 1 operations

The initial service surface is intentionally narrow:

- `Hello` — protocol/service version and elevation state;
- `GetVolumes` — currently available NTFS volumes plus per-volume index status;
- `GetStatus` — aggregate and per-volume status;
- `RebuildVolume` — fresh MFT/metadata namespace snapshot for one volume;
- `SyncVolume` — apply one durable USN journal batch for one volume;
- `Search` — query the persistent indexes and merge results across attached NTFS volumes whose snapshots are currently valid.

There are no file mutation, cleanup, partition, format, TRIM, BitLocker or process-management commands in this protocol.

## Error contract

Expected failures cross the process boundary as structured errors rather than raw exceptions:

- `InvalidRequest`;
- `ProtocolMismatch`;
- `VolumeNotFound`;
- `SnapshotRequired`;
- `ElevationRequired`;
- `Busy`;
- `ResponseTooLarge`;
- `InternalError`.

`ElevationRequired` is specifically produced when NTFS access fails with access denied. Cancelling UAC leaves the desktop process unaffected. `ResponseTooLarge` is retryable with a smaller request when applicable and does not close an otherwise healthy pipe.

### Same-account elevation rule

The current named-pipe model intentionally permits `runas` relaunch only when Windows reports that the current process has a **limited split administrator token**. That is the UAC case where elevation produces a full token for the same Windows user SID, so `PipeOptions.CurrentUserOnly` continues to identify the same account.

Credential-over-the-shoulder elevation from a standard account is deliberately unsupported in this version. In that case `runas` can launch the helper as a different administrator account, which would both break the current-user-only pipe and resolve a different account's LocalAppData. Supporting that scenario requires a separately reviewed service/ACL design rather than silently weakening the pipe ACL.

## Helper location and packaging trust

The desktop build treats `FileOp.Indexer` as a build dependency and copies these host artifacts beside `FileOp.App`:

- `FileOp.Indexer.exe`;
- `FileOp.Indexer.dll`;
- `FileOp.Indexer.deps.json`;
- `FileOp.Indexer.runtimeconfig.json`.

At runtime, the current resolver accepts only the exact `FileOp.Indexer.exe` adjacent to the application and rejects the helper file itself if it is a filesystem reparse point. CI verifies the four bundled artifacts and launches the copy beside the built WinUI application through the real process/pipe handshake.

This location rule is intentionally narrow but is **not** a cryptographic trust claim. Production signed packaging still needs to verify the installed helper/publisher before elevation is offered. The current resolver must not be described as equivalent to Authenticode verification.

## Per-volume persistence and identity

The helper chooses its own storage root under the current Windows user's `LocalApplicationData\FileOp\Index`. An elevated helper does not accept an arbitrary database path from command-line arguments, removing a caller-controlled privileged file-write destination from this protocol. Production hardening should continue to treat the writable per-user storage tree as untrusted input when the helper is elevated.

Each discovered NTFS volume/root pair gets its own SQLite database. This is intentional because the current snapshot indexer clears its target index during a rebuild. A shared multi-volume database would therefore allow rebuilding one drive to erase records for another drive.

Raw NTFS serial numbers are only 32-bit and can collide. Discovery therefore also asks Windows for the stable `\\?\Volume{GUID}\` mount identity. When present, that 128-bit GUID is deterministically hashed into the 64-bit provider volume token used by `FileIdentity`; serial-only identity is retained as a fallback when no volume GUID is available. This makes ordinary cross-volume file identities independent of 32-bit serial collisions without changing the persistent 64-bit schema.

The per-volume database key includes the provider volume identity and current root path. The root remains significant even for a stable physical identity because indexed namespace paths are absolute: if the same disk moves from `D:\` to `E:\`, FileOp takes a fresh path snapshot instead of serving stale paths under the old letter.

## Snapshot validity and concurrency

Rebuild and journal synchronization are serialized per volume with a non-blocking operation gate. A second maintenance command for the same volume receives `Busy` rather than queueing invisibly.

A volume is eligible for service-backed search only while it has a durable NTFS checkpoint and is not being rebuilt. Search briefly acquires the same per-volume gate before checking the checkpoint and reading the index, so a rebuild cannot clear and partially repopulate that database underneath a query. Other valid volumes remain searchable while one volume is busy.

Each volume query is ranked by the same filename/path relevance rules as the SQLite index. The merged cross-volume result set reapplies that relevance score before final name/path tie-breaking so service-backed search does not degrade into alphabetical ordering when multiple indexes participate.

If incremental synchronization determines that a fresh snapshot is required, the service deletes that volume's durable checkpoint before returning `SnapshotRequired`. The invalid state therefore survives helper restarts instead of allowing stale rows to look valid again merely because in-memory state was lost.

The current rebuild implementation temporarily omits the affected volume from search. A future shadow-database rebuild followed by an atomic swap can remove that outage without ever exposing a partial snapshot.

## WinUI integration policy

The desktop now uses the service boundary as its primary search path for the NTFS volume containing the user profile. The integration remains deliberately conservative:

1. Resolve the bundled adjacent helper and start it unelevated.
2. Negotiate the reviewed protocol and discover NTFS volumes.
3. Select the volume containing the user profile as the first-run orchestration target.
4. If no checkpoint exists, build a fresh snapshot; otherwise resume the durable snapshot.
5. Repeatedly call `SyncVolume` until its durable USN cursor stops advancing or the bounded startup catch-up budget is reached. One successful `SyncVolume` call is not considered equivalent to being caught up because each call intentionally processes only one bounded journal batch.
6. Use the native index when its snapshot is searchable. If startup catch-up does not converge within the budget, expose it as active-but-catching-up and continue in the background.
7. Run bounded background catch-up only when the foreground native-operation gate is free, so active searches take priority over maintenance.
8. If a valid existing snapshot can still be searched but journal access returns `ElevationRequired`, retain that snapshot, mark it non-current, stop repeated permission probes and offer explicit helper-only elevation.
9. If the native provider/helper is unavailable, or an invalid snapshot cannot be rebuilt with the current token, build the bounded user-profile crawler fallback.
10. If a native session later disconnects or requires a rebuild that cannot proceed, transition back to the fallback rather than leaving search unavailable.

Engine state controls search-box availability. Input is disabled during initial snapshot construction, fallback crawling, rebuilds and elevation transitions, and is enabled only when a native searchable snapshot or completed fallback snapshot exists.

The fallback is a static crawl snapshot rather than a live watcher, so the coordinator records it as non-current even after the initial crawl finishes.

The first cutover does not automatically create snapshots for every attached NTFS disk. Existing attached-volume databases with valid checkpoints can still participate in service search; automatic first-run orchestration for additional volumes remains a separate lifecycle slice.

### Elevation flow

When the primary volume needs more privilege, the UI exposes an explicit `Enable fast indexing` action while using the crawler fallback, or `Enable live updates` when an older valid native snapshot is usable but cannot synchronize. The desktop itself remains non-elevated. A successful same-account helper relaunch replaces the existing/fallback search source only after the elevated session has produced a valid native state.

Cancelling UAC preserves the existing search mode.
