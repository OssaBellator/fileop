# Windows Disk-I/O attribution provider

## Purpose

`WindowsDiskIoAttributionProvider` is the first concrete Windows implementation of Core's #70 `IDiskIoAttributionProvider`.

It composes the separately reviewed DiskIo layers into one **on-demand, bounded capture** while preserving their evidence boundaries:

- #74 FileOp-owned system trace session control;
- #75/#76 real-time consumer lifecycle and native transport;
- #80/#81 trace timing and loss evidence;
- #84 bounded callback collection;
- #77/#78 immutable record copy and DiskIo completion decoding inside that collector;
- #83 reuse-aware TID/PID lifetime resolution inside that collector;
- #69 portable disk/process aggregation;
- #82 separate event/buffer loss counts in the Core capture result.

This provider does not run continuously and is not yet exposed by the Performance UI.

## Capture phases

A successful capture follows a fixed order:

1. serialize captures within the provider instance;
2. start the dedicated FileOp-owned system trace session;
3. open the real-time consumer;
4. read initial trace evidence and require a positive `PerfFreq`;
5. establish an exact UTC observation window of the requested #70 duration;
6. configure the #84 collector;
7. run blocking `ProcessTrace` on a dedicated managed thread;
8. race the processor against one cancellable duration delay;
9. stop FileOp's owned session on duration or caller cancellation, or accept #84 callback cancellation when the observation cap is reached;
10. drain `ProcessTrace` completely;
11. read final trace evidence while the processing handle is still owned and native state is stable;
12. require final `PerfFreq` to remain valid and equal to the initial trace frequency;
13. aggregate accepted observations through #69;
14. construct #70/#82 `DiskIoCaptureResult`;
15. close the consumer and release the owned session/collector.

There is no polling loop, recurring timer, background monitor or capture that outlives the `CaptureAsync` call.

## Duration boundary

The provider uses one `Task.Delay` for the requested #70 duration. When that delay completes:

- FileOp stops only the session it started and owns;
- waits for the blocking `ProcessTrace` call to return;
- snapshots the bounded collector;
- reads final trace evidence;
- reports `DurationElapsed` unless the collector also reached its normalized observation cap.

If the cap was reached at the duration boundary, `ObservationLimitReached` wins. This is conservative because cap-limited evidence may be incomplete even when the wall-clock window also elapsed.

The #69 report uses the exact configured start/end for a duration-complete capture. Events arriving after the logical end while session shutdown propagates are already filtered by #84.

## Observation-cap boundary

#84 returns `false` once exactly `MaxObservations` normalized, in-window DiskIo completions are accepted. #76 then stops callback delivery at the buffer boundary and `ProcessTrace` returns.

If `ProcessTrace` ends before the duration **without** the collector reporting its cap, the provider fails closed instead of presenting an unexpectedly short trace as a successful duration capture.

For a valid cap stop, the report ends at the latest accepted event timestamp and Core validates that accepted count equals the requested maximum.

## Caller cancellation

Caller cancellation is not converted into an empty successful report.

When cancellation occurs during an active capture, FileOp:

1. stops its owned session;
2. drains `ProcessTrace`;
3. lets the owned consumer close;
4. disposes capture-scoped process/thread attribution handles;
5. propagates `OperationCanceledException`.

A caller cancellation before session creation exits without starting ETW.

## Exceptional cleanup

The provider never intentionally leaves the blocking `ProcessTrace` task unobserved.

If an exception occurs after processing starts:

- it first attempts to stop the owned session;
- if processing is still blocked, it uses the owned `CloseTrace` handle as the fallback cancellation path;
- it awaits the processor before unwinding.

If cleanup itself fails, FileOp surfaces cleanup failure instead of claiming the capture completed normally.

## Trace timing evidence

The provider reads `PerfFreq` immediately after `OpenTraceW` and before event processing. #84/#78 uses that value for DiskIo high-resolution response-time conversion.

After processing drains, FileOp reads trace evidence again. The final frequency must:

- remain positive; and
- match the initial frequency.

FileOp never substitutes `Stopwatch.Frequency`, `QueryPerformanceFrequency`, or a fixed frequency for invalid trace metadata.

## ETW loss evidence

Final #80 evidence maps directly into #82:

- `EventsLost` -> `LostEventCount`;
- `BuffersLost` -> `LostBufferCount`;
- either counter positive -> `DiskIoCaptureLossState.Observed`;
- both zero -> `NoneObserved`.

The counters are never added together. Buffer-only loss remains buffer-only loss.

Any nonzero loss means evidence may be incomplete; it does not by itself prove disk saturation or identify a culprit process.

## Attribution coverage

#84 keeps unresolved process ownership as `Owner = null`, so #69's per-disk attribution coverage remains meaningful. The provider detail also reports the total unresolved-owner count and ignored non-target/out-of-window records.

The Windows-specific unresolved reason map remains in the collection snapshot for a later richer diagnostics surface; it is not collapsed into a fake owner or health score here.

## Availability mapping

Expected controller/consumer failures continue to use the existing policies:

- permission failures -> `PermissionRequired`;
- known session/capacity conflicts -> `SessionUnavailable`;
- non-Windows platform -> `Unsupported`.

Unexpected native statuses, invalid trace metadata, or premature processing termination throw rather than becoming a misleading completed capture.

## Concurrency

A provider instance serializes `CaptureAsync` calls with a semaphore. This prevents the same provider object from competing with itself for FileOp's single dedicated session identity.

The session policy still refuses to reuse or stop an externally existing session whose ownership FileOp cannot prove.

## Deliberate non-goals

This provider does not:

- run periodic or startup ETW captures;
- scan files or disks;
- modify ETW ACLs, group membership or privileges;
- stop another component's trace session;
- aggregate event and buffer loss into one invented count;
- use PID-only attribution;
- persist capture data;
- change indexing-service protocol;
- display process/disk rows in the UI yet;
- disable services, clean the registry, trim working sets or alter power/defrag settings;
- calculate a system-health or optimisation score.

## Validation without GitHub Actions

`tools/verify_windows_disk_io_attribution_provider.py` models duration, cap, caller-cancellation, premature-process and separate-loss outcomes across randomized orchestration states. Source guards pin the session/process/evidence ordering and forbid recurring monitors, guessed clocks and combined loss arithmetic.

Focused .NET tests inject fake trace-control, consumer and collector components through the real #74/#75 lifecycle wrappers. They cover:

- full-duration capture and drain;
- observation-cap capture with buffer-only ETW loss;
- active caller cancellation and cleanup;
- classified consumer-open failure;
- premature `ProcessTrace` completion;
- changed final trace frequency.

Native Windows/.NET execution is still required before release claims about live ETW behavior.

## Next boundary

After this provider is reviewed, the next useful product slice is a **read-only Performance diagnostics integration** that invokes it only on explicit refresh and presents:

- busiest physical disks;
- top attributed process instances;
- attribution coverage;
- ETW event/buffer loss;
- capture stop reason/duration;
- explicit uncertainty when evidence is capped, lost or unattributed.

That UI must not convert a short diagnostic capture into automatic cleanup, service changes, or a generic PC-health score.
