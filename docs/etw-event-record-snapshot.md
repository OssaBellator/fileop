# Immutable ETW event-record snapshots

## Purpose

#76 added the native `OpenTraceW` / `ProcessTrace` transport and intentionally hands the callback sink raw ETW pointers only while the callback is active. This slice creates the next safety boundary: copy the useful `EVENT_RECORD` metadata and user payload into FileOp-owned values **before the callback returns**.

The snapshot does not classify DiskIo events, resolve processes, persist records, or retain native pointers.

## Native layout copied

Microsoft defines `EVENT_RECORD` as:

```text
EVENT_HEADER
ETW_BUFFER_CONTEXT
USHORT ExtendedDataCount
USHORT UserDataLength
PEVENT_HEADER_EXTENDED_DATA_ITEM ExtendedData
PVOID UserData
PVOID UserContext
```

`EVENT_HEADER` is the stable 80-byte prefix. The fixed callback structure is 104 bytes in a 32-bit consumer and 112 bytes in a 64-bit consumer.

FileOp pins these offsets:

| Field | Offset |
|---|---:|
| `EVENT_HEADER` | 0 |
| `ETW_BUFFER_CONTEXT` | 80 |
| `ExtendedDataCount` | 84 |
| `UserDataLength` | 86 |
| `ExtendedData` pointer | 88 |
| `UserData` pointer | 92 x86 / 96 x64 |
| `UserContext` pointer | 96 x86 / 104 x64 |

The snapshot never stores any of the three native pointers.

## Header values

`WindowsEtwEventRecordSnapshot` copies:

- `EVENT_HEADER.Size`;
- header type, flags and event property;
- thread ID and process ID;
- timestamp;
- provider GUID;
- the complete `EVENT_DESCRIPTOR` value: ID, version, channel, level, opcode, task and keyword;
- processor time;
- activity GUID;
- `ETW_BUFFER_CONTEXT.ProcessorIndex` and `LoggerId`;
- extended-data count;
- the event-specific user payload.

Microsoft documents `EVENT_HEADER.Size` as the size of the event record and `UserDataLength` as the size of `UserData`. FileOp uses both as sanity checks before copying the payload.

## Owned payload

The user payload is copied with `Marshal.Copy` into a private `byte[]`. Consumers receive only a `ReadOnlySpan<byte>` view over that private copied buffer.

Tests mutate the original unmanaged payload and event header after `CopyFrom` and verify the snapshot is unchanged. No ETW-owned memory is referenced after construction.

A zero-length payload may have a null `UserData` pointer. A nonzero payload with a null pointer fails closed.

The default copy limit is the native 16-bit `UserDataLength` range, and callers may provide a smaller limit. The limit is checked **before payload dereference**. A payload whose declared length cannot fit within the declared event-record size is also rejected before dereference.

## Extended data

This slice records `ExtendedDataCount` but deliberately does not dereference `ExtendedData`.

The current DiskIo capture does not need stack/SID/related-activity extension blocks for read/write/flush attribution. Pulling those blocks into the evidence model would expand both memory work and parser attack surface without helping the current optimisation question.

If a future feature needs a specific extended-data type, it should add a separate bounded parser for that type rather than implicitly copying every extension.

## Descriptor opcode and the next boundary

Microsoft documents that for **MOF-defined** events, `EVENT_DESCRIPTOR.Opcode` contains the event type value. The snapshot therefore preserves `Opcode` exactly.

This slice does not classify that value. The next DiskIo adapter can separately verify the DiskIo provider GUID, classic-header flags and allowed opcodes, then pass only the known read/write/flush payloads to the existing decoder from #71.

Separating snapshot copying from interpretation means a bug in event classification cannot change the native memory lifetime rules.

## Fail-closed checks

`CopyFrom` rejects:

- a null `EVENT_RECORD` pointer;
- an `EVENT_HEADER.Size` smaller than the 80-byte header;
- a copy limit outside the native 0..65535 range;
- user data larger than the configured copy limit;
- user data that cannot fit within the declared event-record size;
- a nonzero user-data length paired with a null pointer.

The callback pointer itself comes from ETW and is trusted to reference the fixed `EVENT_RECORD` structure for the duration of the callback. This code does not attempt unsafe probing or SEH-style recovery around arbitrary invalid process memory.

## Deliberate non-goals

This snapshot layer does not:

- call `OpenTraceW`, `ProcessTrace` or `CloseTrace`;
- dereference `ExtendedData`;
- follow `UserContext`;
- classify provider GUIDs or opcodes;
- invoke the DiskIo decoder;
- convert response-time ticks;
- resolve issuing threads to processes;
- persist events;
- add protocol or UI behavior;
- calculate bottleneck or health scores.

## Validation without GitHub Actions

`tools/verify_etw_event_record_snapshot.py` mirrors the fixed offsets, size/copy-cap validation and owned-copy semantics across 50,000 randomized records. Repository guards pin the source/tests/docs and ensure the layer does not drift into trace control, event classification, process lookup or extended-data dereference.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Focused .NET tests use allocated native fixtures to cover the same copy boundary without opening a live ETW session.
