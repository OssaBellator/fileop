# Disk I/O response timing evidence

FileOp's bounded DiskIo capture already decodes the classic DiskIo completion event's high-resolution response field using the ETW trace `PerfFreq`. This slice preserves that decoded duration through the collector and summarizes it per physical disk and operation.

For Read, Write and Flush completions separately, FileOp records sample count, minimum, integer-midpoint median, maximum, and nearest-rank p95 when at least five samples exist. Fewer than five samples intentionally leave p95 unavailable rather than presenting an unstable percentile.

The response value is completion-response evidence supplied by the DiskIo event. It is **not a queue-time/service-time decomposition**, does not identify the cause of a delay, and does not by itself establish device saturation or storage pressure. A short capture can also be incomplete when the observation cap is hit or ETW reports event/buffer loss.

Every accepted normalized completion from the Windows collector must have exactly one timing sample before the provider attaches timing summaries. Count mismatch fails closed instead of silently dropping or inventing timings. Empty captures remain valid with empty timing evidence.

This slice does not label a disk as a bottleneck, add a latency threshold, health score, continuous sampler, timer, protocol operation, persistence path, process/service control, or storage action. A later UI interpretation layer can combine disk-specific timing with the existing byte/operation totals, attribution coverage, process-instance ownership, stop reason and ETW loss evidence before making any descriptive bottleneck claim.
