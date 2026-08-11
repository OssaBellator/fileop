# Bounded background process activity evidence

FileOp's next performance layer measures **current process resource activity** instead of treating startup registration, a Run-key entry or a background-app declaration as evidence of performance impact.

This foundation is explicit and on-demand. A capture takes two bounded process-counter frames separated by a default one-second delay and attributes CPU-time change only when the same stable process instance is present in both frames.

## Stable process identity

A process instance is keyed by:

- numeric PID; and
- process start time.

FileOp does not match two frames by PID alone. If PID 40 belongs to one process in the start frame and a different process-start identity in the end frame, the old instance is counted as exited and the new instance as started. No CPU-time delta is invented across PID reuse.

If start time or another required counter cannot be read for a process, that process is not weakly matched. The failed read is counted as inaccessible evidence.

## Measured evidence

For stable instances, FileOp records:

- total processor-time **delta** between the two frames;
- end-of-sample working-set bytes;
- end-of-sample private-memory bytes;
- end-of-sample thread count;
- process start timestamp and display image name;
- whether the row is FileOp's own capture process.

Visible rows are ordered by processor-time delta, then working set, private memory and stable identity for deterministic display. The row budget does not discard hidden CPU evidence: FileOp retains the count and aggregate processor time of other stable matched process instances.

FileOp also records start/end enumeration counts, inaccessible reads, whether the snapshot cap was reached, stable matched instances, instances that appeared/disappeared between frames, and its own capture-process CPU/memory evidence when that stable instance was readable.

## Sampling and observer overhead

The default sampling delay is one second, bounded to 250 ms–3 s. Each frame has an explicit process-snapshot cap and the result has an explicit visible-row cap.

The Windows provider performs exactly two snapshots around one `Task.Delay`. It measures total provider elapsed time with `Stopwatch` and reports time beyond the requested delay as provider overhead. It does not use a periodic timer or continuous sampler.

CPU percentage is intentionally **not** derived in this slice. Individual process counters are read sequentially rather than at one simultaneous instant, so dividing every CPU delta by one nominal wall interval would imply precision the capture does not have.

## Startup/background interpretation boundary

A process start timestamp is context, not proof of startup impact. This bounded sample does not reconstruct historical CPU/memory use at boot and does not claim that a process which started early in the session consumed significant resources during startup.

Conversely, FileOp does not inspect startup registry keys, Startup folders or app-startup registration and label their presence as a performance problem. A later UI can show currently measured activity alongside stable start-time context, but any startup recommendation requires measured evidence that the process is actually relevant to the condition being investigated.

## Partial evidence

The report is explicitly incomplete when either frame reaches the process-snapshot cap or required counters are inaccessible for one or more enumerated processes. Started/exited counts describe only stable readable identities captured in the two frames; they are not machine-wide process creation/exit event counts.

Protected processes may therefore remain outside CPU-delta attribution without disappearing silently from evidence quality.

## Scope boundary

This slice does not:

- enumerate startup registry keys or Startup folders;
- inspect or mutate startup-app enablement;
- stop, suspend, kill, reprioritize or restart processes;
- disable services or scheduled tasks;
- persist process activity history;
- add a timer, watcher or background sampler;
- create an impact/health score;
- infer CPU percentage from the approximate frame interval;
- add an indexing-helper protocol operation.

The provider is a read-only Windows performance provider. Protocol remains v8, and any future management action requires a separately reviewed authorization/recovery boundary.
