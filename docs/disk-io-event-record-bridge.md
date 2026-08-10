# DiskIo EVENT_RECORD decoder bridge

## Purpose

#76 owns native real-time ETW transport and #77 copies each callback record into FileOp-owned immutable values. #71 already owns the documented DiskIo read/write/flush payload layouts, while #72 owns event pointer-width selection and high-resolution response-time conversion.

This slice connects those existing boundaries without adding another parser.

## MOF event identity

Microsoft documents that for **MOF-based** ETW events, event identity is contained in `EVENT_DESCRIPTOR.Opcode`, not `EventDescriptor.Id`.

`WindowsDiskIoEventRecordBridge` therefore uses the immutable snapshot's `Descriptor.Opcode` as the classic DiskIo event type and deliberately ignores `Id` for identity.

Only these #71 completion opcodes are candidates:

- `10` → read completion;
- `11` → write completion;
- `14` → flush completion.

A wrong provider GUID or wrong opcode returns `false` and is ignored before pointer-width or timing metadata is validated. This matters because a real system trace can contain records that are not part of FileOp's DiskIo evidence even when the capture enables only a narrow event group.

## Claimed DiskIo completion validation

Once a record claims both the #71 DiskIo provider GUID and one of the three completion opcodes, the bridge treats malformed metadata as an error instead of silently dropping it.

It requires:

- `EVENT_HEADER_FLAG_CLASSIC_HEADER` (`0x0100`);
- exactly one of `EVENT_HEADER_FLAG_32_BIT_HEADER` (`0x0020`) or `EVENT_HEADER_FLAG_64_BIT_HEADER` (`0x0040`);
- a positive trace performance-counter frequency;
- a payload long enough for #71's completion layout.

The classic-header requirement prevents FileOp from interpreting a different event encoding with the classic MOF DiskIo byte layout merely because it reused the same provider/opcode values.

Pointer-width ambiguity is delegated to `WindowsDiskIoTraceMetadata.FromEventHeaderFlags`; FileOp still never guesses from its own process architecture.

## Existing decoder reuse

The bridge does **not parse payload bytes**. After metadata validation it calls:

```text
WindowsDiskIoEventDecoder.TryDecodeCompletion(...)
```

with:

- the snapshot provider GUID;
- `Descriptor.Opcode` as the MOF event type;
- the pointer size resolved by #72;
- the snapshot's owned `UserData` span.

#71 therefore remains the single owner of:

- DiskIo provider GUID;
- read/write/flush opcode constants;
- 32/64-bit payload offsets;
- transfer-size rules;
- issuing-thread ID extraction;
- raw `HighResResponseTime` extraction;
- flush zero-byte behavior;
- truncated-payload rejection.

This bridge intentionally contains no `BinaryPrimitives`, `Marshal`, `BitConverter` or duplicated byte offsets.

## Decoded event value

A successful bridge result contains:

- the immutable ETW event timestamp value;
- processor index;
- logger ID;
- the #71 `WindowsDiskIoCompletion` value;
- response time converted by #72 using the supplied `PerfFreq`.

The response-time conversion is evidence, not a storage-health score. A later capture/report layer can aggregate distributions and disclose event loss rather than turning one completion into a broad performance claim.

## Fail-open versus fail-closed boundary

The bridge is intentionally asymmetric:

| Record | Result |
|---|---|
| wrong provider | ignored (`false`) |
| DiskIo provider, non-completion opcode | ignored (`false`) |
| claimed completion, missing classic-header flag | error |
| claimed completion, both/neither pointer flags | error |
| claimed completion, non-positive `PerfFreq` | error |
| claimed completion, truncated payload | error |
| valid completion | decoded |

This keeps unrelated ETW traffic from becoming noise while preventing corrupted target evidence from disappearing silently.

## Deliberate non-goals

This bridge does not:

- open, process or close an ETW trace;
- copy native callback memory;
- parse payload fields itself;
- convert the event timestamp to `DateTimeOffset`;
- resolve issuing-thread IDs to processes;
- create `DiskIoEventObservation` values;
- aggregate disk/process attribution;
- add protocol/UI behavior;
- persist events;
- calculate a disk bottleneck, health score or optimisation recommendation.

The next layer can use the decoded issuing-thread ID plus a lifetime-aware process resolver before creating #69 observations. Keeping that separate avoids attributing I/O to a reused PID or to the event-header process ID when the completion payload already exposes the issuing thread.

## Validation without GitHub Actions

`tools/verify_disk_io_event_record_bridge.py` models provider/opcode selection, classic/pointer flags, #71 payload minimums and #72 timing conversion across 50,000 randomized completion records. Repository guards require reuse of #71/#72/#77 and forbid byte parsing, process lookup and trace control in the bridge.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Focused .NET tests cover 32-bit read, 64-bit write, flush, Opcode-vs-Id identity, unrelated-event ignore behavior and every target-record fail-closed path.
