# Storage I/O bottleneck evidence

FileOp's explicit bounded DiskIo capture now has enough disk-specific traffic, response-timing, process-ownership, attribution-coverage and trace-quality evidence to provide **investigation cues** for storage I/O. This layer deliberately does not turn those cues into a universal bottleneck verdict.

## Comparative cues

For one completed capture, FileOp derives three independent facts:

- **Highest observed eligible p95** — the largest existing Read/Write/Flush p95 among physical-disk operation groups that already have the response-timing contract's minimum five samples. No new latency threshold is introduced.
- **Largest observed byte-volume disk** — the physical disk with the most transferred Read+Write bytes in the capture. Ties use operation count and then physical-disk number only for deterministic display.
- **Largest identified owner by observed bytes** — the visible process instance with the most attributed Read+Write bytes. Existing attribution guarantees the largest identified owner on a disk is visible because at least one byte-ranked owner row is retained; timing does not promote a hidden owner.

These cues are intentionally independent. The disk with the highest observed p95 can differ from the disk with the most observed bytes, and the largest identified owner can be on another disk. FileOp does not combine them into a score.

## Evidence quality

The summary preserves `DiskIoCaptureResult.EvidenceMayBeIncomplete`. If the observation cap was reached or ETW reported loss, the UI states that the comparative cues are partial rather than hiding the values or pretending they cover the whole requested window.

A completed capture with no accepted completions has no comparative cues. A capture can have byte-volume/owner cues but no p95 cue when every operation has fewer than five timing samples or when a compatibility result has no typed response timing.

## What the cues do not prove

The **highest observed p95** means only the highest eligible p95 inside this bounded capture. It is not a slow-device threshold, SLA, device-health assessment, queue-depth measurement or proof of saturation.

The **largest observed byte-volume disk** means only that FileOp saw the most transferred bytes there during this sample. It does not prove that the disk was saturated or limiting application performance.

The **largest identified owner** identifies the issuing process instance associated with the most observed attributed bytes. Issuing ownership is association, not proof that the process caused device response delay.

FileOp therefore labels the UI section **Storage I/O bottleneck evidence**, but explicitly states that it does not apply a universal slow-disk threshold or declare a bottleneck from these values alone.

## Scope boundary

This slice adds no extra ETW capture, background sampler, timer, device query, SMART query, queue-depth counter, process lookup, threshold, health/performance score, recommendation, persistence, protocol operation, process/service control or storage action. Protocol remains v8.

A later action/recommendation layer would need its own evidence contract. In particular, recommending device maintenance, process intervention or workload changes would require evidence that the proposed action addresses the measured condition rather than relying on these comparative cues alone.
