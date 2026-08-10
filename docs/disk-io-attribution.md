# Disk-I/O attribution evidence

## Purpose

FileOp should identify which process instances are responsible for **observed physical-disk I/O** before it suggests that storage is a performance bottleneck. Generic process I/O accounting is not sufficient evidence because it does not identify the physical disk that handled an operation.

This slice defines the Core evidence/aggregation contract only. It does **not** start an ETW session, add a background sampler, require elevation, or expose a disk-health score.

## Windows evidence boundary

The intended Windows provider is the system/kernel DiskIo ETW stream, not `GetProcessIoCounters` or a generic process-I/O performance counter.

Microsoft's DiskIo event schema gives read/write completion events:

- physical `DiskNumber`;
- `TransferSize` in bytes;
- operation type (read or write);
- issuing thread identity;
- disk-response timing metadata.

DiskIo init events can identify the issuing thread for a request, and flush completion events identify the physical disk but do not carry a transfer-size field.

A future provider therefore has enough disk-specific evidence to normalize read/write/flush completions into `DiskIoEventObservation` records. It must resolve thread ownership at event time and preserve unresolved ownership rather than guessing.

System/kernel tracing has an explicit privilege and session-lifecycle boundary. A future provider must separately review:

- system-logger permission requirements;
- behavior when the required ETW session cannot be started;
- coexistence with existing system loggers;
- exact foreground capture duration and event/buffer budget;
- cancellation and shutdown cleanup;
- trace-session naming/ownership;
- process/thread lifetime and PID/TID reuse;
- observed tracing overhead.

Those concerns are intentionally not hidden inside `FileOp.Indexer` in this contract-only slice.

## Core observation contract

`DiskIoEventObservation` represents one normalized, disk-specific event:

```text
Timestamp
PhysicalDiskNumber
Operation = Read | Write | Flush
TransferBytes
Owner?
```

Read/write transfer bytes must be non-negative. Flushes must use zero transfer bytes because the DiskIo flush completion schema does not provide a transfer-size field.

Every observation must lie inside the report's declared capture window. Invalid operation values, negative byte counts, impossible owner PIDs, owner start times after the event, or non-zero flush byte counts fail closed.

## Process-instance identity

An attributed owner carries:

```text
ProcessId
StartedAt?
ImageName?
```

`ProcessId + StartedAt` is the strong grouping key when a provider can resolve the process start time. The same PID with different start times is treated as different process instances.

If start time is unavailable, FileOp may still keep the PID/name attribution for the bounded capture, but `HasStableInstanceIdentity` remains false. The UI/provider must not imply that such a row is durable identity across captures.

Image name is display evidence, not the grouping key.

## Aggregation semantics

`DiskIoAttributionAnalyzer` groups observations first by physical disk, then by process instance.

Per physical disk it retains:

- total read/write bytes;
- read/write/flush operation counts;
- explicitly unattributed read/write bytes and operation counts;
- top identified process rows;
- exact aggregate bytes/operations for identified owners omitted by the row limit;
- omitted identified-owner count;
- identified-byte coverage over all observed read/write bytes.

The default visible-owner limit is 12 per physical disk and the hard contract limit is 100. Truncating owner rows never removes their bytes or operations from the disk totals.

Owners are ordered deterministically by:

1. observed transfer bytes descending;
2. observed operation count descending;
3. process ID ascending;
4. process start time ascending.

A visible owner's percentage is its observed read/write bytes divided by all observed read/write bytes on that physical disk. Flush-only activity has no byte share because flush completion events do not provide transferred bytes.

## Unresolved ownership is first-class evidence

Thread/process resolution can fail because an issuing thread or process exits, access is denied, trace metadata is incomplete, or the future provider misses the lifetime event needed for a strong mapping.

FileOp must not distribute those bytes across known processes or silently discard them. Unresolved operations are retained in the physical-disk denominator as **unattributed** evidence.

`AttributionCoveragePercent` therefore describes the fraction of observed read/write bytes that have identified process ownership. It is not a confidence score and says nothing about whether the disk itself is saturated.

## What this contract does not claim

This model does not by itself establish a storage bottleneck. High byte volume does not prove high latency, queue pressure, user-visible delay, or that an owner should be terminated/disabled.

This slice does not:

- start ETW;
- poll process counters;
- use `GetProcessIoCounters` as disk evidence;
- use registry/startup presence as activity evidence;
- infer disk saturation from transfer bytes alone;
- calculate an opaque health score;
- terminate, suspend, reprioritize or disable a process/service;
- persist process-I/O history;
- add a continuous background sampler.

A later provider/UI slice should combine a bounded disk-specific capture with explicit timing/coverage evidence before describing a bottleneck.

## Validation without GitHub Actions

`tools/verify_disk_io_attribution.py` models the same aggregation rules over randomized multi-disk/process observations and guards the repository contract:

- physical-disk separation;
- read/write/flush accounting;
- zero-byte flush semantics;
- strong PID + process-start identity;
- explicit weak identity when start time is absent;
- preserved unattributed bytes;
- exact hidden-owner rollup under top-N truncation;
- deterministic ordering and observed-byte share;
- no generic `GetProcessIoCounters`, process performance-counter, timer, registry or service-control implementation in this slice;
- no ETW provider/session code falsely implied by the contract-only PR.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Focused .NET regressions cover the same Core boundary when the Windows/.NET local gate is available.
