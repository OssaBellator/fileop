# Performance Disk I/O attribution UI

## Purpose

FileOp's Performance panel now exposes the bounded Windows DiskIo attribution provider added in #86 as an **explicit user action**.

The user starts a short capture with **Capture disk I/O**. FileOp does not run this capture as part of ordinary Performance refresh, Storage Optimize refresh, startup, or a background monitor.

The purpose is diagnostic evidence: show which physical disks were active, which stable process instances FileOp could attribute to that traffic, how much traffic remained unattributed, and whether ETW reported incomplete trace evidence.

## Independent from the file index

Disk I/O attribution is system-wide ETW evidence. It is not scoped to the current indexed NTFS root and does not require the native file index to be ready.

This is intentionally different from reclaim analysis and the existing lightweight Search/Storage/index-database probes:

- reclaim advice can be unavailable because the native index is busy, disconnected, or the app is using a fallback source;
- ordinary index-backed Performance diagnostics can be unavailable for the same reason;
- the explicit Disk I/O capture remains available because it observes Windows physical-disk/process activity independently of that index state.

The Optimize/Performance surface therefore remains enterable even when native reclaim analysis is unavailable. The storage portion explains its unavailable state while the Disk I/O card can still run its own provider and report `Unsupported`, `PermissionRequired`, `SessionUnavailable`, or a completed capture.

## Explicit capture only

There are now two separate Performance actions:

- **Refresh diagnostics** — existing bounded foreground Search, Storage, database and journal evidence;
- **Capture disk I/O** — #86's default two-second system-wide ETW attribution capture.

The normal refresh event does not invoke `CaptureDiskIoAttributionAsync`. Loading or refreshing Storage Optimize also does not start ETW.

While a Disk I/O capture is active, its button stays disabled even if unrelated storage/index state updates mark ordinary diagnostics ready again.

## Summary evidence

A completed capture shows:

- capture stop reason (`Duration` or `Observation cap`);
- accepted normalized DiskIo completion count;
- ETW event loss and buffer loss **as separate counters**;
- observed window duration.

#85 remains the source-of-truth correction for event loss: `LostEventCount` originates from `TRACE_LOGFILE_HEADER.EventsLost`, not the documented-unused `EVENT_TRACE_LOGFILEW.EventsLost` member.

When #70 reports `EvidenceMayBeIncomplete`, the UI explicitly says the short sample may be incomplete because the observation cap was reached or ETW reported loss. It does not convert those conditions into a health grade or an optimisation score.

## Physical-disk rows

For each physical disk reported by #69 the UI shows:

- read bytes;
- write bytes;
- total read/write/flush operations;
- attribution coverage percentage when byte traffic exists;
- unattributed bytes;
- bytes/count folded into additional identified owners when the per-disk owner display cap hides lower-ranked process instances.

Unattributed traffic remains visible. FileOp does not assign unresolved traffic to `System`, the current foreground app, or another arbitrary owner.

## Process-instance rows

Visible owner rows include:

- physical disk number;
- image name when available, otherwise PID;
- PID;
- process start time when available;
- read bytes;
- write bytes;
- total operations;
- observed byte share on that disk.

Process start time is shown because #69/#83 identity is **PID + process creation time**, not PID alone. This keeps a reused PID from looking like one continuous process instance.

## Context changes

The ETW sample is system-wide, so changing the indexed storage source does not invalidate a completed Disk I/O result and does not cancel a capture merely because reclaim/index context changed.

Application shutdown still cancels through the engine lifetime token. A provider instance also serializes its own captures, and the UI prevents a second click while one capture is active.

If the user temporarily leaves Optimize while a capture completes, the result can remain attached to the hidden Performance view and be visible on return; it is not discarded as stale index-scoped evidence.

## Permission and availability

The UI does not modify ETW ACLs, local group membership, privileges, services, registry settings, memory policy, power settings or defragmentation settings to make a capture succeed.

Expected provider availability states are displayed as evidence/status text. In particular, a `PermissionRequired` result remains a permission result rather than triggering elevation or membership changes automatically.

## Deliberate non-goals

This UI does not:

- continuously monitor disk/process I/O;
- start ETW during ordinary Performance or Storage refresh;
- require an indexed volume for system-wide attribution;
- hide event/buffer loss inside one total;
- hide unattributed traffic;
- use PID-only identity;
- persist a process activity history;
- recommend deleting files based on a two-second process sample;
- automatically stop services or processes;
- clean the registry or free RAM;
- alter TRIM/defrag/power/pagefile configuration;
- calculate a generic PC-health score.

## Validation without GitHub Actions

`tools/verify_performance_disk_io_ui.py` models loss/cap/coverage presentation across randomized evidence states and source-guards the UI architecture:

- the capture button is explicit;
- the normal refresh path does not invoke ETW;
- storage/index availability does not gate the Disk I/O handler;
- Storage `SetUnavailable` / ordinary refresh readiness do not reset or enable the Disk I/O card incorrectly;
- event and buffer loss remain separate;
- attribution coverage/unattributed traffic and process start time are surfaced;
- no periodic monitor, PID-only lookup, registry action or health score is introduced.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`.

Native WinUI compilation and live Windows ETW capture still require the Windows/.NET release gate and are not claimed from the current sandbox.
