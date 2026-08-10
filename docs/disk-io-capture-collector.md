# Disk-I/O capture collector

## Purpose

This layer composes the already-reviewed event-processing pieces without starting or stopping ETW itself. It is the callback sink used by the future Windows `IDiskIoAttributionProvider`.

The collector turns native callback records into bounded #69 `DiskIoEventObservation` values by reusing:

- #77 immutable `EVENT_RECORD` copying;
- #78 DiskIo completion classification/decoding;
- #83 reuse-aware issuing-thread/process attribution.

It deliberately does not duplicate native structure or DiskIo payload parsing.

## Configuration boundary

A collector is configured exactly once before `ProcessTrace` begins with:

- the positive `PerfFreq` read from the trace through #80/#81;
- a UTC observation-window start;
- a UTC observation-window end;
- the #70 maximum normalized observation count supplied at construction.

Callbacks before configuration fail closed. FileOp does not guess a performance-counter frequency from its own process.

## Event callback flow

For each callback:

1. #77 copies `EVENT_RECORD` metadata and bounded user data while ETW owns the pointer;
2. #78 ignores unrelated providers/opcodes and decodes only classic DiskIo completion types;
3. #83 resolves the completion's issuing TID against the event FILETIME;
4. events outside the declared observation window are ignored;
5. in-window completions become `DiskIoEventObservation` values;
6. unresolved ownership remains `Owner = null` and its explicit resolver status is counted;
7. when the exact normalized observation cap is accepted, the callback returns `false` so #76 requests consumer cancellation.

The collector never accepts an observation beyond its configured cap.

## Unresolved attribution evidence

The collection snapshot keeps a count per `WindowsDiskIoOwnerResolutionStatus`. This preserves why ownership was unavailable—for example access failure, cache ceiling, ID-reuse state uncertainty, or creation-time mismatch—rather than assigning that I/O to an arbitrary process.

Those reason counts are Windows-specific evidence for the later provider/UI. #69's portable aggregation still receives unresolved observations as `Owner = null` so attribution coverage remains visible in the disk report.

## Ignored events

`IgnoredEventCount` includes:

- non-DiskIo events delivered to the consumer;
- DiskIo non-completion events;
- otherwise valid completion records outside the configured observation window.

Ignored records do not consume the normalized observation budget.

## Observation cap

`ObservationLimitReached` becomes true only after exactly `MaxObservations` normalized in-window completions have been accepted.

Once set:

- later event callbacks return `false` without dereferencing another event pointer;
- buffer callbacks return `false`;
- the snapshot remains fixed at the configured maximum count.

The future provider maps this state to #70 `DiskIoCaptureStopReason.ObservationLimitReached`.

## Scope boundary

This collector does not:

- call `StartTrace`, `OpenTrace`, `ProcessTrace`, `CloseTrace` or `ControlTrace`;
- own duration timers or cancellation tokens;
- read live ETW loss counters;
- aggregate observations into a report;
- add event and buffer loss counters;
- enumerate processes or use PID-only attribution;
- persist evidence;
- change indexing-service protocol or UI;
- calculate a performance-health score.

Those responsibilities remain separately reviewable in the provider and presentation layers.

## Validation without GitHub Actions

`tools/verify_disk_io_capture_collector.py` models bounded mixed event streams and source-guards reuse of #77/#78/#83 while forbidding trace control, duplicate byte parsing, process lookup shortcuts, and timers in the collector.

Focused .NET fixtures exercise resolved and unresolved owners, unrelated records, observation-window filtering, exact cap cancellation, and single-shot configuration without starting a real ETW session.

`tools/test-local.ps1 -OfflineOnly` now also invokes the previously merged DiskIo resolver, trace-evidence, evidence-lifecycle and separate-loss verifiers so these composition prerequisites are part of the ordinary local gate.
