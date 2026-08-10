# Owned disk-I/O ETW controller lifecycle

## Purpose

#69–#73 define disk-I/O attribution, bounded capture policy, native DiskIo payload decoding, trace timing/pointer metadata, and a dedicated system-logger identity/error policy. This slice crosses the first native ETW boundary: allocate `EVENT_TRACE_PROPERTIES`, call `StartTraceW`, and stop **only a session started successfully by this controller** with `ControlTraceW(EVENT_TRACE_CONTROL_STOP)`.

It deliberately does not open or consume the real-time trace yet. `OpenTraceW` / `ProcessTrace` remain a separate review boundary because callbacks, cancellation, event loss and process ownership resolution have different failure modes from controller ownership.

## Native ABI source

FileOp's interop mirrors the current Windows SDK definitions:

- `CONTROLTRACE_ID` is an unsigned 64-bit value;
- `WNODE_HEADER` contains two eight-byte unions and is 48 bytes under the Windows ABI;
- `EVENT_TRACE_PROPERTIES.LoggerThreadId` is a native `HANDLE`, so the managed layout uses `IntPtr`;
- `WNODE_FLAG_TRACED_GUID = 0x00020000`;
- `EVENT_TRACE_CONTROL_STOP = 1`.

The managed `EVENT_TRACE_PROPERTIES` structure is therefore 116 bytes in a 32-bit process and 120 bytes in a 64-bit process. FileOp uses `Marshal.SizeOf` rather than assuming either size.

The native functions are imported from `Sechost.dll`, which is the documented ETW controller DLL on Windows 8.1 and later. FileOp's supported Windows floor is Windows 10 1809.

## Properties allocation

`WindowsDiskIoTracePropertiesBuffer` allocates one zero-filled native block containing:

```text
EVENT_TRACE_PROPERTIES
UTF-16 session name + NUL
```

The start buffer sets:

- `Wnode.BufferSize` to the full native block size;
- `Wnode.Guid` to FileOp's stable system-session GUID;
- `Wnode.ClientContext = 1` for the QPC clock;
- `Wnode.Flags = WNODE_FLAG_TRACED_GUID`;
- `BufferSize = 16 KB`;
- `MinimumBuffers = 0`;
- `MaximumBuffers = 64`;
- `LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE`;
- `EnableFlags = EVENT_TRACE_FLAG_DISK_IO | EVENT_TRACE_FLAG_NO_SYSCONFIG`;
- `LogFileNameOffset = 0` because this is real-time only;
- `LoggerNameOffset = sizeof(EVENT_TRACE_PROPERTIES)`.

The 16 KB buffer target reflects the small fixed DiskIo completion events and keeps each ETW buffer modest. `MinimumBuffers = 0` allows ETW to select its processor-driven minimum; `MaximumBuffers = 64` is a requested target, not a hard machine-wide memory ceiling because ETW may raise it when required by its adjusted minimum.

The stop buffer is newly zeroed and carries only the control identity/offset fields required for STOP. It never reuses possibly mutated output from the start call, and `LogFileNameOffset` remains zero.

## Ownership rule

`WindowsDiskIoTraceSessionController.Start()` has only three outcomes:

1. `StartTraceW` succeeds and returns a nonzero `CONTROLTRACE_ID`: FileOp creates a `WindowsDiskIoOwnedTraceSession` for that exact ID.
2. The #73 expected availability errors occur: FileOp returns `PermissionRequired` or `SessionUnavailable` with **no owned session**.
3. Any other native status is an unexpected `Win32Exception`.

A successful native status paired with control-trace ID `0` fails closed because the SDK defines zero as invalid.

Crucially, failed start attempts never invoke STOP. A colliding session name/GUID does not become FileOp-owned merely because it uses FileOp's public identifiers.

## Stop rule

An owned session stops by the exact nonzero `CONTROLTRACE_ID` returned by the successful `StartTraceW` call. `ControlTraceW` receives `InstanceName = NULL`, so cleanup does not rediscover a session by name.

Expected terminal results are:

- `ERROR_SUCCESS` → `Stopped`;
- `ERROR_MORE_DATA` → `StoppedWithTruncatedStatistics` because Microsoft documents that STOP has already occurred even though the properties buffer could not hold all returned statistics;
- `ERROR_WMI_INSTANCE_NOT_FOUND` → `AlreadyStopped`;
- `ERROR_ACTIVE_CONNECTIONS` → `StopInProgress`.

Once one of those results is observed, later `Stop()`/`Dispose()` calls are idempotent and do not issue another native stop.

Unexpected STOP results are also terminal for the controller's command state. The first call throws the corresponding `Win32Exception`; a later call does **not** issue another potentially ambiguous native STOP and instead reports that a terminal stop failure already occurred. This is especially important because some malformed-buffer errors can coincide with ETW having already stopped the session.

`Dispose()` is a fallback cleanup path for an active owned session. The future capture provider should still call `Stop()` explicitly in its `finally` path so cleanup status can be incorporated into the capture result instead of relying only on disposal.

## Real-time consumer boundary

Microsoft warns not to leave a real-time ETW session running without a real-time consumer because ETW buffers events and wastes system resources. This controller is **not wired into the desktop or protocol in this slice**. The subsequent provider integration must:

1. start the owned session;
2. immediately open the matching real-time consumer;
3. process only for #70's bounded capture window/event cap;
4. stop the owned controller session in `finally`;
5. close/finish consumer processing and disclose event loss.

There is therefore no product path in this PR that starts a real-time session and leaves it unattended.

## Privilege boundary

The controller does not request elevation or alter Windows policy. `ERROR_ACCESS_DENIED` remains the explicit #73 `PermissionRequired` result. It does not:

- launch a `runas` process;
- add the user to `Performance Log Users`;
- edit ETW ACLs;
- modify `EtwMaxLoggers`;
- stop/reuse a pre-existing session after `ERROR_ALREADY_EXISTS`;
- control services or processes.

Any later helper/elevation design must be reviewed separately from this low-level controller.

## Validation without GitHub Actions

`tools/verify_disk_io_trace_controller.py` models the start/ownership/stop state machine and verifies native-layout arithmetic for both pointer widths. Repository guards pin the Microsoft SDK constants, Sechost imports, zero-filled fresh control buffers, exact handle-based STOP, tests and the absence of event-consumer/UI/elevation behavior in this controller-only slice.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Focused .NET tests use an injected fake trace-control API, so they can verify lifecycle semantics without starting a real ETW session. A real Windows ETW smoke test remains pending the native Windows/.NET gate and later consumer provider.
