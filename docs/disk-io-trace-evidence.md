# DiskIo trace timing and loss evidence

## Purpose

The DiskIo attribution pipeline now has native transport (#76), immutable event copies (#77), completion decoding (#78) and lifetime-aware process attribution (#79). Before those pieces are composed into a user-visible bottleneck report, FileOp also needs evidence about the **trace itself**: the timing frequency used by DiskIo response ticks and whether ETW reported data loss.

This slice reads those values from the `EVENT_TRACE_LOGFILEW` state already owned by #76. It does not start a trace or change the consumer lifecycle.

## Evidence fields

`WindowsDiskIoTraceEvidence` preserves three values separately:

- `PerformanceCounterFrequency` from `TRACE_LOGFILE_HEADER.PerfFreq`;
- `EventsLost` from `EVENT_TRACE_LOGFILEW.EventsLost`;
- `BuffersLost` from `TRACE_LOGFILE_HEADER.BuffersLost`.

FileOp does **not** sum the two loss counters. They are separate ETW evidence and a future report should disclose them separately rather than inventing a combined loss total.

`HasReportedLoss` is true when either counter is nonzero. It is only a quality flag; it does not estimate how many FileOp-relevant DiskIo completions are missing.

`HasValidPerformanceCounterFrequency` is true only for a positive frequency. A zero/negative frequency is not replaced with the current machine stopwatch frequency or any fixed clock assumption.

## Explicit layout

#76 already pins the native `EVENT_TRACE_LOGFILEW` / `TRACE_LOGFILE_HEADER` ABI and exposes the established `PerfFreq` and `BuffersLost` offsets.

The remaining consumer-level loss field is:

- `EVENT_TRACE_LOGFILEW.EventsLost`: offset **396** in x86;
- `EVENT_TRACE_LOGFILEW.EventsLost`: offset **416** in x64/arm64.

These values follow from the same explicit layout used by #76:

- x86 `EVENT_TRACE_LOGFILEW` size: 416 bytes;
- x64/arm64 size: 448 bytes;
- x86 callback area begins at 384;
- x64/arm64 callback area begins at 400.

The reader accesses the retained unmanaged block directly with fixed-width `Marshal.Read*` operations and converts the two DWORD loss counters to the full unsigned 32-bit range.

## Sampling timing

The native structure is ETW-owned/mutated while a processing call is active. This reader therefore defines a data extraction primitive, not a concurrency policy.

The later provider should sample it at stable lifecycle points:

1. after `OpenTraceW` returns, read `PerfFreq` before decoding DiskIo `HighResResponseTime` values;
2. after `ProcessTrace` returns, read final event/buffer loss counters before releasing the retained consumer state.

It should **not** read the structure concurrently with an active `ProcessTrace` call merely to update a live counter.

A subsequent integration slice can expose the evidence through #76's native adapter while enforcing those lifecycle points.

## Interpretation boundary

A positive `PerfFreq` is required for #72 response-time conversion. The evidence record itself does not throw for zero frequency because retaining the raw trace state is useful for explaining why timing evidence is unusable; #72 remains the component that rejects invalid frequency when conversion is attempted.

Nonzero loss means the trace is incomplete. It does not prove disk saturation, does not identify which processes were affected, and does not justify a cleanup recommendation by itself.

The future provider should report:

- captured event count;
- attribution coverage;
- `EventsLost`;
- `BuffersLost`;
- whether timing frequency was valid;

alongside any latency/throughput attribution result.

## Deliberate non-goals

This slice does not:

- call `OpenTraceW`, `ProcessTrace` or `CloseTrace`;
- mutate the retained native buffer;
- aggregate or normalize the loss counters into one number;
- guess a missing clock frequency;
- decode event payloads;
- resolve processes;
- create a performance-health score;
- add protocol or UI behavior;
- persist trace evidence.

## Validation without GitHub Actions

Focused .NET tests write deterministic frequency/loss values into #76's allocated native logfile fixture and verify the evidence reader extracts the exact unsigned values at the documented offsets.

A portable verifier mirrors the x86/x64 offsets and guards against loss-counter summation, clock fallback, event decoding, process lookup and trace-control behavior in this read-only layer.
