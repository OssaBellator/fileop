# Disk-I/O system-session policy

## Purpose

The disk-I/O attribution work in #69–#72 defines what evidence means, bounds capture work, decodes Windows `DiskIo` completion payloads, and normalizes trace pointer/timing metadata. The next native boundary is how FileOp owns an ETW controller session.

This slice defines the session identity/configuration and expected Win32-result mapping. It still does **not** call `StartTrace`, `OpenTrace`, `ProcessTrace` or `ControlTrace`.

## Dedicated system logger, not legacy NT Kernel Logger

Current Microsoft `StartTrace` guidance prefers `EVENT_TRACE_SYSTEM_LOGGER_MODE` for new system loggers. Using `KERNEL_LOGGER_NAME` or `SystemTraceControlGuid` to create the legacy NT Kernel Logger is deprecated for new code because those reserved identities conflict with other components.

FileOp therefore plans one dedicated system-logger session:

- session name: `FileOp Disk I/O Diagnostics`;
- `LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE`;
- `EnableFlags = EVENT_TRACE_FLAG_DISK_IO | EVENT_TRACE_FLAG_NO_SYSCONFIG`;
- `WNODE_HEADER.ClientContext = 1` (QueryPerformanceCounter clock);
- requested session GUID: `GUID_NULL`, allowing ETW to assign the session GUID;
- no `KERNEL_LOGGER_NAME`;
- no `SystemTraceControlGuid`.

`EVENT_TRACE_FLAG_DISK_IO` is the only event group FileOp needs for the current Read/Write/Flush decoder. `EVENT_TRACE_FLAG_NO_SYSCONFIG` prevents the otherwise automatic system-configuration rundown from adding unrelated events to this short diagnostic capture.

The app's supported Windows floor is newer than Windows 8, where multiplexed SystemTraceProvider sessions and `EVENT_TRACE_SYSTEM_LOGGER_MODE` are supported.

## Session name and collision behavior

Microsoft recommends a descriptive, deterministic session name and specifically advises against random suffixes just to make a session unique. ETW sessions are a limited cross-process resource.

FileOp uses one fixed descriptive name. If `StartTrace` returns `ERROR_ALREADY_EXISTS`, FileOp reports `SessionUnavailable`.

This policy intentionally does **not** stop or reuse the colliding session based only on its name. A stale FileOp session and an unrelated/spoofed session are not distinguishable by name alone. The later native controller may stop only a trace handle/session that the current capture has positively established it owns.

This is stricter than automatically deleting a presumed orphan, but it avoids terminating another diagnostic tool's session.

## Expected StartTrace failures

Only documented, expected availability conditions are converted to `DiskIoCaptureStatus`:

| Win32 result | FileOp result | Meaning |
|---|---|---|
| `ERROR_ACCESS_DENIED` (5) | `PermissionRequired` | Current security context cannot control the required trace session. |
| `ERROR_ALREADY_EXISTS` (183) | `SessionUnavailable` | FileOp's descriptive session name or GUID is already active; do not take it over. |
| `ERROR_NO_SYSTEM_RESOURCES` (1450) | `SessionUnavailable` | The system/system-logger session limit or ETW resources are exhausted. |

`ERROR_NO_SYSTEM_RESOURCES` can mean the maximum of eight system loggers or the broader ETW session limit has been reached. FileOp must **not** modify `EtwMaxLoggers`, stop another component's logger, or broaden privileges automatically to make room.

Other errors such as `ERROR_INVALID_PARAMETER` or `ERROR_BAD_LENGTH` are not availability states. They indicate an unexpected provider/configuration defect and must remain visible as a failure rather than being presented as a normal unavailable condition.

## Stop/cleanup results

For a session that FileOp has already established it owns, the cleanup layer recognizes:

- `ERROR_SUCCESS` → stopped;
- `ERROR_WMI_INSTANCE_NOT_FOUND` (4201) → already stopped/not running;
- `ERROR_ACTIVE_CONNECTIONS` (2402) → stop is already in progress.

Microsoft documents `ERROR_ACTIVE_CONNECTIONS` specifically for `EVENT_TRACE_CONTROL_STOP` as meaning that the trace session is already stopping.

Other cleanup errors are not swallowed. In particular, `ERROR_ACCESS_DENIED`, malformed-property errors and unexpected failures remain visible to the provider so cleanup failures cannot be silently reported as success.

## Resource/privilege boundary

A system logger is a limited cross-process resource. Microsoft recommends starting ETW sessions only when specifically needed and stopping them as soon as the scenario completes.

FileOp's existing #70 capture contract already provides the matching product boundary:

- explicit user/request-driven capture only;
- default two-second window;
- hard five-second window;
- normalized-event cap;
- cancellation token;
- no recurring cadence.

This session policy does not request elevation, add users to `Performance Log Users`, edit trace-control ACLs, modify registry logger limits, or reuse/stop unowned sessions.

## Deliberate non-goals

This slice does not:

- P/Invoke ETW APIs;
- allocate `EVENT_TRACE_PROPERTIES` native buffers;
- start/open/process/stop a trace;
- create a background logger;
- resolve issuing threads to processes;
- add a named-pipe/protocol operation;
- add UI;
- persist capture results;
- infer disk saturation from event volume.

The next native-controller slice can mechanically apply this policy to `EVENT_TRACE_PROPERTIES`, `StartTraceW`, `ControlTraceW` and later `OpenTraceW`/`ProcessTrace`, while retaining #70's capture budget and #71/#72's decoder/metadata boundaries.

## Offline validation

`tools/verify_disk_io_system_session_policy.py` verifies constants, expected error classification, stop-result classification, deterministic session identity, and the source/docs/tests boundary. It also guards against legacy `KERNEL_LOGGER_NAME`/`SystemTraceControlGuid`, random session naming, ETW P/Invoke, registry changes, elevation and process control appearing in this policy-only slice.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Focused .NET tests cover the same contract when a Windows/.NET test host is available.
