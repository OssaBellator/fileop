# Native disk-I/O ETW consumer adapter

## Purpose

#75 defined the ownership/cancellation contract for an ETW processing handle without declaring the large native consumer structure. This slice implements that native boundary behind the existing lifecycle:

- explicit `EVENT_TRACE_LOGFILEW` ABI sizing/offsets;
- `OpenTraceW`;
- `ProcessTrace`;
- `CloseTrace`;
- rooted `EventRecordCallback` and `BufferCallback` delegates;
- callback cancellation and exception containment.

It still does **not** interpret or retain `EVENT_RECORD` data. Event copying/decoding remains the next independently reviewable boundary.

## Microsoft consumer contract

For a real-time `EventRecordCallback` consumer, Microsoft documents the following `EVENT_TRACE_LOGFILEW` setup:

- zero-initialize the structure;
- leave `LogFileName` null;
- set `LoggerName` to the active real-time session name;
- set `ProcessTraceMode` to `PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD`;
- set `EventRecordCallback`;
- optionally set `BufferCallback`;
- call `OpenTraceW`, then `ProcessTrace` with the returned processing handle;
- close that processing handle with `CloseTrace`, never with the collection-session controller handle.

#75 already pins the process-mode bits and keeps the processing handle distinct from #74's `CONTROLTRACE_ID`.

## Explicit native layout

The native call deliberately does **not** depend on CLR sequential-struct alignment. `WindowsDiskIoTraceLogfileBuffer` allocates one zero-filled unmanaged block and writes only the required `EVENT_TRACE_LOGFILEW` inputs at explicit pointer-width offsets.

Important sizes under the Windows default eight-byte packing are:

| Structure | x86 | x64/arm64 |
|---|---:|---:|
| `EVENT_TRACE_HEADER` | 48 | 48 |
| `EVENT_TRACE` | 88 | 88 |
| `TIME_ZONE_INFORMATION` | 172 | 172 |
| `TRACE_LOGFILE_HEADER` | 272 | 280 |
| `EVENT_TRACE_LOGFILEW` | 416 | 448 |

The x86 `EVENT_TRACE` fields end at byte 84 but the structure is padded to its eight-byte alignment, so its native size is 88 bytes.

The explicit `EVENT_TRACE_LOGFILEW` input offsets are:

- x86: `LoggerName=4`, `ProcessTraceMode=20`, `BufferCallback=384`, `EventRecordCallback=400`, `Context=408`;
- x64/arm64: `LoggerName=8`, `ProcessTraceMode=28`, `BufferCallback=400`, `EventRecordCallback=424`, `Context=440`.

The buffer starts fully zeroed, so `LogFileName`, output fields and reserved/native state remain zero unless ETW fills them during `OpenTraceW`.

The same explicit layout records future evidence offsets:

- `TRACE_LOGFILE_HEADER.PerfFreq`: overall `EVENT_TRACE_LOGFILEW` offset 360 on x86 / 376 on x64/arm64;
- `TRACE_LOGFILE_HEADER.BuffersLost`: overall offset 380 / 396.

Those fields will be useful to the later evidence layer, but this transport slice does not yet publish or interpret them.

## Function imports

FileOp targets Windows 10 1809 or later, so the consumer imports are from `sechost.dll`:

- `OpenTraceW` with `SetLastError=true`;
- `ProcessTrace`;
- `CloseTrace`.

`OpenTraceW` receives the explicit unmanaged buffer as `IntPtr`, avoiding runtime structure marshalling. Failure is detected through `INVALID_PROCESSTRACE_HANDLE`; the adapter captures the Win32 error immediately with `Marshal.GetLastPInvokeError()` and returns it to #75's existing open-failure classifier.

`ProcessTrace` is called for exactly one real-time processing handle and receives that 64-bit handle by reference. No temporary handle array or mixed-session processing is used.

The logger-name string is unmanaged only for the duration of `OpenTraceW`. After `OpenTraceW` returns, subsequent processing uses the returned processing handle; FileOp does not retain the input string allocation.

## Callback lifetime

The native callback fields are explicit function pointers created with `Marshal.GetFunctionPointerForDelegate`. The corresponding delegates are instance fields on the native adapter, so they remain strongly rooted while the adapter/processing handle is in use.

The sink receives raw pointers only synchronously while ETW is executing the callback:

```text
bool OnEventRecord(IntPtr eventRecord)
bool OnBuffer(IntPtr logfile)
```

This PR does not decode `EVENT_RECORD`, copy its user payload, or retain ETW-owned memory after either callback returns.

The next event-adapter slice must copy every field/payload it needs into FileOp-owned values before returning from `OnEventRecord`.

## Cancellation and draining

The callback sink may return `false` from either callback contract.

For an event callback, FileOp records cancellation and suppresses subsequent event sink calls. At the end of the current ETW buffer, `BufferCallback` returns `FALSE`, causing `ProcessTrace` to report the cancellation through the #75 lifecycle.

For a buffer callback, FileOp returns `FALSE` immediately.

#75 still provides the independent prompt `CloseTrace` path for cancellation from another thread while `ProcessTrace` is blocked. The callback cancellation path is complementary; it does not add polling or a background timer.

A successful `CloseTrace`, including `ERROR_CTX_CLOSE_PENDING`, may release the processing handle while the blocking `ProcessTrace` call is still draining previously queued events. The native adapter tracks that separately with `_processActive`: it cannot be reopened and cannot reset callback state until the prior `ProcessTrace` invocation has actually returned.

## Callback exceptions

Managed callback exceptions must never cross an unmanaged ETW callback boundary.

The adapter therefore:

1. catches any exception from the sink;
2. captures the first exception with `ExceptionDispatchInfo`;
3. marks callback cancellation;
4. suppresses later event sink calls;
5. returns `FALSE` from the buffer callback;
6. after native `ProcessTrace` exits, rethrows the original captured exception with its identity/stack information preserved.

A null `EVENT_RECORD` pointer is treated as a contained `InvalidDataException` and follows the same shutdown path.

This keeps Microsoft’s `ERROR_NOACCESS` callback-exception status from becoming FileOp’s normal control flow. If native ETW itself returns `ERROR_NOACCESS` without a captured managed fault, #75 continues to report it as a native failure.

## Ownership and non-goals

This adapter does not:

- start or stop the collection session;
- take over an existing ETW session;
- request elevation or modify ETW ACLs;
- poll for events;
- decode classic DiskIo event type or payload;
- resolve thread IDs to processes;
- read process metadata;
- add protocol/UI wiring;
- persist trace events;
- compute disk bottleneck scores or optimisation recommendations.

Those boundaries remain separate so FileOp can distinguish transport correctness from evidence interpretation.

## Validation without GitHub Actions

`tools/verify_disk_io_native_consumer.py` checks x86/x64 ABI arithmetic, explicit pointer-width offsets, callback-state behavior, native imports, rooted delegate setup, callback-fault containment, drain/reopen ownership, focused tests and scope guards. It is wired into `tools/test-local.ps1 -OfflineOnly`.

Focused .NET tests validate the actual zeroed unmanaged buffer and the exact values written at the documented offsets, then directly exercise managed callback containment and the close-pending drain guard without opening a live ETW session.

A real `OpenTraceW`/`ProcessTrace` smoke test still requires the Windows/.NET/native test environment and the later provider integration. No live ETW execution is claimed by this slice.
