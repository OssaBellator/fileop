# DiskIo trace timing and loss evidence

## Purpose

The DiskIo attribution pipeline needs evidence about the **trace itself**: the timing frequency used by DiskIo response ticks and whether ETW reported data loss.

This layer reads those values from the retained `EVENT_TRACE_LOGFILEW` state owned by the native consumer. It does not start a trace or change the consumer lifecycle.

## Evidence fields

`WindowsDiskIoTraceEvidence` preserves three values separately:

- `PerformanceCounterFrequency` from `TRACE_LOGFILE_HEADER.PerfFreq`;
- `EventsLost` from `TRACE_LOGFILE_HEADER.EventsLost`;
- `BuffersLost` from `TRACE_LOGFILE_HEADER.BuffersLost`.

FileOp does **not** sum the two loss counters. They are separate ETW evidence and a report should disclose them separately rather than inventing a combined loss total.

`HasReportedLoss` is true when either counter is nonzero. It is only a quality flag; it does not estimate how many FileOp-relevant DiskIo completions are missing.

`HasValidPerformanceCounterFrequency` is true only for a positive frequency. A zero/negative frequency is not replaced with the current machine stopwatch frequency or any fixed clock assumption.

## Correct event-loss source

Microsoft documents `TRACE_LOGFILE_HEADER.EventsLost` as the number of events lost during the trace session, primarily due to insufficient trace-buffer memory or very high event rate.

`EVENT_TRACE_LOGFILEW` also has a member named `EventsLost`, but Microsoft documents that consumer-level member as **“Not used.”** FileOp therefore must not treat that field as loss evidence.

An earlier implementation incorrectly read the unused consumer member. The corrected reader now uses only `TRACE_LOGFILE_HEADER.EventsLost`, and regression fixtures deliberately write a nonzero decoy value into the unused consumer slot to prove it is ignored.

## Explicit layout

#76 pins the native `EVENT_TRACE_LOGFILEW` / `TRACE_LOGFILE_HEADER` ABI.

Within `TRACE_LOGFILE_HEADER`, `EventsLost` is the third `ULONG` in the 16-byte union beginning after `BuffersWritten`, so its relative offset is **48 bytes** from the start of the header.

Because `EVENT_TRACE_LOGFILEW.LogfileHeader` begins at different absolute offsets by pointer width:

- x86 `TRACE_LOGFILE_HEADER.EventsLost`: absolute offset **160**;
- x64/arm64 `TRACE_LOGFILE_HEADER.EventsLost`: absolute offset **168**.

The existing trace offsets remain:

- x86 `PerfFreq`: absolute offset 360;
- x64/arm64 `PerfFreq`: absolute offset 376;
- x86 `BuffersLost`: absolute offset 380;
- x64/arm64 `BuffersLost`: absolute offset 396.

The reader accesses the retained unmanaged block with fixed-width `Marshal.Read*` operations and converts the DWORD loss counters to the full unsigned 32-bit range.

The unused `EVENT_TRACE_LOGFILEW.EventsLost` slot at 396 on x86 / 416 on x64 is **not** part of FileOp's evidence model.

## Sampling timing

The native structure is ETW-owned/mutated while a processing call is active.

The provider samples it only at stable lifecycle points:

1. after `OpenTraceW` returns, read `PerfFreq` before decoding DiskIo `HighResResponseTime` values;
2. after `ProcessTrace` returns, read final event/buffer loss counters before releasing the retained consumer state.

It should **not** read the structure concurrently with an active `ProcessTrace` call merely to update a live counter.

#81 enforces those stable-read lifecycle points at the native adapter boundary.

## Interpretation boundary

A positive `PerfFreq` is required for #72 response-time conversion. The evidence record itself retains the raw frequency, while the later provider rejects an invalid/non-positive value rather than guessing a clock.

Nonzero trace-header loss means the trace is incomplete. It does not prove disk saturation, does not identify which processes were affected, and does not justify a cleanup recommendation by itself.

A capture report should disclose:

- captured normalized event count;
- attribution coverage;
- `TRACE_LOGFILE_HEADER.EventsLost`;
- `TRACE_LOGFILE_HEADER.BuffersLost`;
- whether timing frequency was valid;
- observation-cap stop state.

## Deliberate non-goals

This layer does not:

- use the documented “Not used” `EVENT_TRACE_LOGFILEW.EventsLost` member as evidence;
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

Focused .NET tests write deterministic frequency/loss values into #76's allocated native logfile fixture and verify exact unsigned extraction from the `TRACE_LOGFILE_HEADER` offsets.

A dedicated decoy test writes a nonzero value into the unused consumer `EventsLost` slot and verifies FileOp still reports zero events lost when `TRACE_LOGFILE_HEADER.EventsLost` is zero.

The portable verifier mirrors x86/x64 header offsets, requires the trace-header source, forbids the old consumer offset from production reader code, and guards against loss summation, clock fallback, event decoding, process lookup and trace-control behavior in this read-only layer.
