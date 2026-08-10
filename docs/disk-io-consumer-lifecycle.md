# Disk-I/O real-time consumer lifecycle

## Purpose

#74 establishes ownership of the ETW controller session. This slice defines the separate lifetime of the real-time **processing handle** returned by `OpenTrace`, including expected open/process/close outcomes and cancellation semantics.

It deliberately does **not** declare the large native `EVENT_TRACE_LOGFILEW` structure or P/Invoke `OpenTraceW`/`ProcessTrace`/`CloseTrace` yet. The production adapter will implement this lifecycle in the next native-consumer slice.

## Processing mode

FileOp requires:

- `PROCESS_TRACE_MODE_REAL_TIME = 0x00000100`;
- `PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000`.

The combined mode is `0x10000100`.

Microsoft strongly recommends `EVENT_RECORD` for new consumers, and real-time mode is mandatory when `LoggerName` identifies a real-time session.

FileOp does not request `PROCESS_TRACE_MODE_RAW_TIMESTAMP` in this lifecycle. That means the eventual `EVENT_HEADER.TimeStamp` delivered by `ProcessTrace` uses ETW's converted system-time form, while #72 separately converts DiskIo's schema-specific `HighResResponseTime` using the session `PerfFreq`. The two timing fields have different semantics and are not conflated.

## Open semantics

`OpenTrace` failure is represented by `INVALID_PROCESSTRACE_HANDLE`, defined on Windows Vista+ as `(UINT64)UINTPTR_MAX`. FileOp additionally rejects handle `0`, which `CloseTrace` documents as invalid.

A successful open therefore requires a nonzero handle that is not the architecture-specific sentinel.

Expected open availability failures are:

- `ERROR_ACCESS_DENIED` → `PermissionRequired`;
- `ERROR_WMI_INSTANCE_NOT_FOUND` → `SessionUnavailable` because the real-time collection session is not running/available.

Unexpected configuration/native errors remain failures instead of being shown as ordinary availability states.

The consumer uses the same deterministic session name as #73/#74. A failed open never owns a processing handle and therefore never calls `ProcessTrace` or `CloseTrace`.

## ProcessTrace semantics

Microsoft documents `ProcessTrace` as blocking until processing ends. FileOp distinguishes:

- `ERROR_SUCCESS` → completed processing;
- `ERROR_CANCELLED` → the consumer cancelled processing, normally by its future buffer callback returning FALSE;
- `ERROR_WMI_INSTANCE_NOT_FOUND` → the real-time collection session ended/disappeared;
- other errors → unexpected failure.

In particular, `ERROR_NOACCESS` is **not** mapped to cancellation. Microsoft documents it when an exception occurs in one of the event-delivery callbacks. The later native callback layer must catch managed exceptions before they cross the unmanaged callback boundary, store the first callback fault, request processing shutdown, and surface the original managed fault after `ProcessTrace` returns.

## Close/cancellation semantics

For real-time processing on FileOp's Windows 10+ floor, `CloseTrace` can be called before the blocking `ProcessTrace` call returns.

Expected successful close outcomes are:

- `ERROR_SUCCESS` → `Closed`;
- `ERROR_CTX_CLOSE_PENDING` (7007) → `ClosePending`.

Microsoft explicitly defines `ERROR_CTX_CLOSE_PENDING` as a successful close: `ProcessTrace` drains events already queued in its buffers, accepts no new events, and then returns.

This makes the processing handle the preferred prompt cancellation mechanism for a bounded capture. The capture provider can independently stop the owned controller session in `finally`; it does not need a polling timer or repeated close calls.

Close outcomes are terminal and idempotent. Unexpected `CloseTrace` errors are also terminal for command issuance: FileOp surfaces the first native error and does not issue a second ambiguous close on the same processing handle.

## Controller versus consumer handles

The two ETW handles are deliberately different types/lifetimes:

- #74's `CONTROLTRACE_ID` owns the **collection session** and is stopped with `ControlTraceW`;
- this slice's processing handle owns the **consumer connection** and is closed with `CloseTrace`.

FileOp never calls `CloseTrace` with a `StartTraceW` control ID and never calls `ControlTraceW` with a processing handle.

## Concurrency boundary

`ProcessTrace` is allowed to block on one thread while another thread calls `CloseTrace` on the same real-time processing handle, as documented for Windows Vista and later.

`WindowsDiskIoOwnedTraceConsumer.Process()` therefore does not hold its state lock while the native processing call runs. `Close()` uses a short command-state lock so concurrent/repeated close requests can issue at most one native close command.

After close begins/succeeds, new `Process()` calls are rejected. A `ProcessTrace` already in progress is expected to return as the native close completes/drains.

## Privilege boundary

Real-time event consumption has its own ETW permission requirement. An access denial stays `PermissionRequired`; this layer does not:

- request elevation;
- modify ETW security descriptors;
- add the user to `Performance Log Users`;
- alter registry/logger limits;
- stop another component's session.

## Deliberate non-goals

This slice does not:

- declare `EVENT_TRACE_LOGFILEW`;
- P/Invoke `OpenTraceW`, `ProcessTrace`, or `CloseTrace`;
- decode `EVENT_RECORD` callbacks;
- decide the classic DiskIo event-type field;
- resolve issuing thread IDs to processes;
- add timers/background polling;
- add protocol or UI;
- persist I/O activity.

## Validation without GitHub Actions

`tools/verify_disk_io_consumer_lifecycle.py` models open/process/close ownership across randomized native results. Repository guards pin process modes, architecture-specific invalid handles, availability mapping, no-access callback-fault treatment, close-pending semantics, tests/docs, and the absence of real consumer P/Invoke in this slice.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Focused .NET tests use an injected fake consumer API and therefore do not start a real ETW consumer.
