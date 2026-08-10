# Windows DiskIo trace metadata

## Purpose

The DiskIo completion decoder (#71) intentionally accepts an explicit pointer width and preserves `HighResResponseTime` as raw counter ticks. A real ETW consumer must derive both decoding width and response-time units from the trace metadata, not from the FileOp process architecture or a fixed clock assumption.

This slice defines that small conversion boundary. It still does not start, stop, open or process an ETW trace session.

## Pointer-width evidence

Microsoft documents two `EVENT_HEADER.Flags` values relevant to pointer properties:

- `EVENT_HEADER_FLAG_32_BIT_HEADER` — event pointer properties are 4 bytes;
- `EVENT_HEADER_FLAG_64_BIT_HEADER` — event pointer properties are 8 bytes.

`WindowsDiskIoTraceMetadata.FromEventHeaderFlags` accepts the already-decoded presence of those two flags and requires **exactly one** to be present.

If both flags are present, the event metadata is contradictory and FileOp fails closed.

If neither flag is present, FileOp also fails closed. Microsoft's general TDH sample falls back to the decoder process pointer size in this ambiguous case, but FileOp deliberately does not use that fallback for disk attribution because a wrong width would shift the later DiskIo native fields while still producing plausible numbers.

A future `EVENT_RECORD` adapter will be responsible for testing the actual numeric flag bits and passing their presence to this object.

## Performance-counter frequency

`TRACE_LOGFILE_HEADER.PerfFreq` is the frequency of the high-resolution performance counter used for trace timing metadata.

`WindowsDiskIoTraceMetadata` requires this frequency to be positive. It does not use `Stopwatch.Frequency`, CPU MHz, wall-clock ticks or a hard-coded 10 MHz assumption.

For DiskIo `HighResResponseTime`, FileOp converts raw counter ticks to `TimeSpan` ticks using:

```text
timeSpanTicks = round(
    responseTicks × TimeSpan.TicksPerSecond / PerfFreq
)
```

Rounding is deterministic nearest rounding with midpoint values away from zero. Disk response durations are non-negative, so the midpoint rule only affects positive half-tick values.

The multiplication is performed with `decimal` precision to avoid overflowing a 64-bit intermediate when `responseTicks` is large. If the converted result would exceed `TimeSpan.MaxValue`, conversion fails closed instead of wrapping or saturating silently.

## Separation from event timestamps

This conversion is only for DiskIo's `HighResResponseTime` duration field.

`EVENT_HEADER.TimeStamp` has separate semantics determined by the trace session's clock configuration and by whether raw timestamps were requested. This slice does not convert event timestamps and does not assume that an event header timestamp can be divided by `PerfFreq` in every trace mode.

The future consumer should obtain an absolute event timestamp through the ETW consumption rules appropriate to its `ProcessTraceMode`/session clock, then pass that timestamp into FileOp's normalized attribution observation.

## Decoder integration

The intended flow is:

```text
EVENT_RECORD flags + TRACE_LOGFILE_HEADER.PerfFreq
        ↓
WindowsDiskIoTraceMetadata
        ↓ PointerSize
WindowsDiskIoEventDecoder
        ↓ raw completion evidence
ConvertHighResolutionResponseTime(...)
```

The metadata object contains no provider/session handle and owns no native resource. It can therefore be tested independently from privileged system-logger control.

## Deliberate non-goals

This slice does not:

- hard-code the numeric `EVENT_HEADER_FLAG_*` constants;
- P/Invoke ETW APIs;
- start or consume the NT Kernel Logger;
- choose a trace clock type;
- convert `EVENT_HEADER.TimeStamp`;
- resolve issuing thread IDs to processes;
- add a protocol operation or UI;
- persist traces;
- add timers/background sampling;
- infer storage bottleneck severity.

## Validation without GitHub Actions

`tools/verify_disk_io_trace_metadata.py` models the pointer-width and high-resolution duration conversion rules across randomized pointer-flag/frequency/tick combinations. Repository guards require:

- exact one-of-two pointer flag semantics;
- positive performance-counter frequency;
- decimal conversion using `TimeSpan.TicksPerSecond`;
- deterministic midpoint rounding;
- `TimeSpan` overflow rejection;
- decoder integration using the derived pointer width;
- explicit documentation that response-time conversion is separate from event timestamp conversion;
- absence of P/Invoke/session-control, `Stopwatch.Frequency`, CPU-frequency and timer shortcuts.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Focused .NET tests provide the compiler/runtime gate when a Windows development environment is available.
