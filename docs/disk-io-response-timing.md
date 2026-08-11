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

Issuing-process association still does not prove that the process caused the device response duration. The process timing is evidence about completions associated with that captured process instance, not a causal attribution of storage delay.

## Process timing UI presentation

The existing **Top attributed process instances** table now includes a wrapped response-timing column. The table remains explicitly **byte-ranked**: process timing does not choose, reorder or promote rows.

Each visible process row shows Read, Write and Flush summaries using the same sample-count/minimum/median/p95/maximum formatter as the per-disk timing table. A compatibility result with no typed process-bound timing says **No typed timing** instead of inventing zero latency.

The process-timing status reports how many response-duration samples are attached to the visible byte-ranked rows and separately reports identified-hidden and unresolved-owner sample counts. Those hidden/unresolved samples remain outside the visible process table; they are not promoted because their timing is high.

The UI repeats the evidence boundary that issuing-process association does not prove the process caused the device response duration. It adds no process latency ranking, slow/fast label, score, recommendation, automatic capture, poller, control action or storage action.

## Performance UI presentation

The explicit **Capture disk I/O** result shows a separate response-timing table beside the existing traffic/ownership evidence. Each physical disk has independent Read, Write and Flush cells containing sample count, minimum, median, p95 when eligible, and maximum. Operations with no samples say so rather than displaying zero latency.

Durations below 1 ms are displayed in microseconds, durations below 1 second in milliseconds, and longer durations in seconds. These are display units only, not performance thresholds.

The UI repeats the evidence boundary: completion response is not queue time or service time, p95 requires at least five samples, and timing alone is not a bottleneck verdict. A completed legacy/compatibility result with accepted operations but no typed timing evidence explicitly says that no latency value is inferred.

This UI slice does not add a latency threshold, health score, continuous sampler, timer, protocol operation, persistence path, process/service control, storage action or automated recommendation. The existing observation-cap, ETW-loss and attribution-coverage evidence remains visible alongside timing so later interpretation work can stay evidence-correct.
