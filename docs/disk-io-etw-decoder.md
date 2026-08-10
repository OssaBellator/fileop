# Windows DiskIo ETW decoder

## Purpose

The disk-I/O attribution contract (#69) and capture policy (#70) deliberately stop before Windows ETW session control. This slice adds the next narrow boundary: deterministic decoding of the documented NT Kernel Logger `DiskIo` **completion** payloads into typed Windows evidence.

It still does not start, stop, open or consume an ETW session.

## Accepted provider and event types

`WindowsDiskIoEventDecoder` accepts only the Windows `DiskIo` class GUID:

```text
3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c
```

and only these documented completion event types:

- `10` — Read;
- `11` — Write;
- `14` — FlushBuffers.

Other provider GUIDs and other DiskIo event types return `false` instead of being reinterpreted. In particular, ReadInit/WriteInit/FlushInit event types `12`, `13`, and `15` are not completion observations and are not accepted by this decoder.

## Pointer-width rule

Two properties in the current read/write payload (`FileObject` and `Irp`) and one property in the flush payload (`Irp`) are ETW pointer values. Pointer fields consume either 4 or 8 bytes depending on the event header's pointer-width flags.

Microsoft's TDH guidance uses the same rule: an event with `EVENT_HEADER_FLAG_32_BIT_HEADER` uses 4-byte pointers; an event with `EVENT_HEADER_FLAG_64_BIT_HEADER` uses 8-byte pointers.

This decoder therefore requires the caller to supply pointer size `4` or `8`. Any other value fails closed. The future ETW consumer will derive this value from `EVENT_RECORD.EventHeader.Flags`; this decoder does not guess based on the FileOp process architecture.

## Read/write layout

For current Windows `DiskIo_TypeGroup1`, FileOp consumes only documented fields while advancing over pointer-sized properties:

```text
Offset                         Field
0                              DiskNumber:u32
4                              IrpFlags:u32
8                              TransferSize:u32
12                             Reserved:u32
16                             ByteOffset:i64
24                             FileObject:pointer
24 + pointerSize               Irp:pointer
24 + 2*pointerSize             HighResResponseTime:u64
32 + 2*pointerSize             IssuingThreadId:u32
```

Minimum payload length is therefore:

- 44 bytes for 4-byte pointers;
- 52 bytes for 8-byte pointers.

Read and Write use the same type-group layout. `TransferSize` is widened to FileOp's signed 64-bit byte count without changing its unsigned 32-bit value.

## Flush layout

For current Windows `DiskIo_TypeGroup3`:

```text
Offset                         Field
0                              DiskNumber:u32
4                              IrpFlags:u32
8                              HighResResponseTime:u64
16                             Irp:pointer
16 + pointerSize               IssuingThreadId:u32
```

Minimum payload length is:

- 24 bytes for 4-byte pointers;
- 28 bytes for 8-byte pointers.

The Windows schema contains no transfer-size field for flush completion, so FileOp always emits `TransferBytes = 0`. The decoder never invents bytes from IRP flags, response ticks, or another field.

## Preserved evidence

A decoded `WindowsDiskIoCompletion` contains:

- physical disk number;
- Read / Write / Flush operation;
- transfer bytes (`0` for Flush);
- raw `HighResResponseTime` ticks;
- issuing thread ID;
- IRP flags;
- byte offset for Read/Write, otherwise null.

Trailing bytes after the documented fields are tolerated. This keeps decoding forward-compatible with additive payload tails while preventing them from shifting the fields FileOp already understands.

Recognized completion payloads shorter than the documented minimum fail closed with `InvalidDataException`.

## Response-time semantics

`HighResResponseTime` is a performance-counter tick count measured from I/O initiation to completion. It is **not milliseconds**.

This decoder preserves the raw tick value only. A future ETW provider may convert it to elapsed time only when it also has the trace/session performance-counter frequency needed for that conversion. The decoder does not assume `Stopwatch.Frequency`, wall-clock ticks, or a fixed hardware frequency.

## Ownership boundary

Completion events expose an `IssuingThreadId` on current supported Windows versions. This decoder preserves that thread ID but does not resolve it to a process.

A later provider must perform thread/process lifetime resolution at capture time and may produce an unattributed observation when the issuing thread/process has exited, access is denied, or identity cannot be proven. It must not substitute the callback thread, ETW consumer process, or another PID.

## Deliberate non-goals

This slice does not:

- P/Invoke `StartTrace`, `OpenTrace`, `ProcessTrace`, `ControlTrace` or `CloseTrace`;
- choose or own the NT Kernel Logger session;
- request elevation;
- resolve thread IDs to process instances;
- convert response ticks to milliseconds;
- add a protocol operation or UI;
- persist disk-I/O events;
- start a timer or background sampler;
- infer storage bottleneck severity.

## Validation without GitHub Actions

`tools/verify_disk_io_etw_decoder.py` mirrors the documented byte layouts and exercises randomized 4-byte/8-byte Read/Write/Flush payloads. Repository guards require:

- the exact DiskIo GUID and event type values;
- separate TypeGroup1 and TypeGroup3 offsets;
- exact pointer-width handling;
- fail-closed truncation;
- zero flush transfer bytes;
- raw response-tick preservation;
- unit fixtures for both pointer widths;
- absence of ETW session-control APIs in the decoder slice.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Windows/.NET regression tests provide the compiler/runtime gate when a Windows development environment is available.
