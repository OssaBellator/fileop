# Disk I/O response timing evidence

FileOp's bounded DiskIo capture decodes the classic DiskIo completion event's high-resolution response field using the ETW trace `PerfFreq`, preserves that decoded duration through the collector and summarizes it per physical disk and operation.

For Read, Write and Flush completions separately, FileOp records sample count, minimum, integer-midpoint median, maximum, and nearest-rank p95 when at least five samples exist. Fewer than five samples intentionally leave p95 unavailable rather than presenting an unstable percentile.

The response value is completion-response evidence supplied by the DiskIo event. It is **not a queue-time/service-time decomposition**, does not identify the cause of a delay, and does not by itself establish device saturation or storage pressure. A short capture can also be incomplete when the observation cap is hit or ETW reports event/buffer loss.

Every accepted normalized completion from the Windows collector must have exactly one timing sample before the provider attaches timing summaries. Count mismatch fails closed instead of silently dropping or inventing timings. Empty captures remain valid with empty timing evidence.

## Process provenance

Each accepted timing sample retains the exact optional `DiskIoProcessIdentity` from the **same resolver result** used to build its corresponding attribution observation. This adds no second process lookup. If issuing-thread/process resolution fails, both the attribution event and its timing sample retain `Owner = null` rather than assigning the timing to a guessed process.

Before either disk attribution or timing aggregation runs, the Windows provider verifies the two bounded evidence sequences index by index. Timestamp, physical disk number, operation and optional process identity must all match; any count or provenance mismatch fails closed. This prevents process-timing work from relying on two arrays that merely happen to contain the same number of entries.

The provenance comparison deliberately uses the full captured process identity on each completion. Existing attribution grouping continues to use its reviewed stable PID + process-start key, so PID reuse remains distinct when start evidence is available.

## Process-bound timing aggregation

Process-bound timing is derived only after the attribution report has already selected its **byte-ranked visible attribution owners**. Timing never chooses, reorders or promotes process rows. For each physical disk, FileOp preserves the attribution report's visible-owner order and calculates Read, Write and Flush response-timing summaries only for those same stable process instances.

Timing samples for identified owners hidden by the attribution top-N limit remain in separate `OtherIdentified*Samples` counts. Timing samples whose issuing process could not be resolved remain in separate `Unattributed*Samples` counts. Visible-owner, hidden-identified and unattributed Read/Write/Flush counts are all cross-checked against the attribution report before process timing can be attached to a completed capture.

A hidden process with a larger response duration therefore cannot become visible merely because its timing is high. The process-timing attachment also requires ordinary per-disk response timing to be attached first and verifies that disk totals, visible owner order/counts, hidden counts, unattributed counts and the completed event total all describe the same capture.

This evidence layer **does not yet expose process-specific response timing** and the Performance UI **does not yet display process-specific timing**. It preserves typed evidence for a later, separately reviewed presentation layer. Even when process timing is displayed, issuing-process association will not prove that the process caused the device response duration.

## Performance UI presentation

The explicit **Capture disk I/O** result shows a separate response-timing table beside the existing traffic/ownership evidence. Each physical disk has independent Read, Write and Flush cells containing sample count, minimum, median, p95 when eligible, and maximum. Operations with no samples say so rather than displaying zero latency.

Durations below 1 ms are displayed in microseconds, durations below 1 second in milliseconds, and longer durations in seconds. These are display units only, not performance thresholds.

The UI repeats the evidence boundary: completion response is not queue time or service time, p95 requires at least five samples, and timing alone is not a bottleneck verdict. A completed legacy/compatibility result with accepted operations but no typed timing evidence explicitly says that no latency value is inferred.

This UI slice does not add a latency threshold, health score, continuous sampler, timer, protocol operation, persistence path, process/service control, storage action or automated recommendation. The existing observation-cap, ETW-loss and attribution-coverage evidence remains visible alongside timing so a later interpretation layer can stay evidence-correct.
