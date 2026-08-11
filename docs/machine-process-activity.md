# Bounded machine process activity evidence

FileOp's next performance layer measures **current machine process resource activity** instead of treating startup registration, a Run-key entry, or a background-app declaration as evidence of performance impact.

The provider enumerates readable machine processes. It does **not** classify a process as foreground or background, and it does not infer that a currently running process is a startup-impact process. “Startup/background diagnostics” is the later investigation use case for this evidence, not an operating-system classification produced by this provider.

A capture is explicit and bounded: two process-counter frames separated by a default one-second delay.

## Stable process identity

A process instance is keyed by numeric PID plus process start time. FileOp never matches two frames by PID alone.

If one process owns PID 40 in the first frame and another process-start identity owns PID 40 in the second frame, the old instance is counted as disappeared and the new instance as appeared. No CPU-time delta is invented across PID reuse.

If start time or another required counter cannot be read, that process is not weakly matched. The read failure is counted as inaccessible evidence.

## Measured evidence

For stable readable process instances, FileOp records:

- total processor-time **delta** between the two frames;
- end-of-sample working-set bytes;
- end-of-sample private-memory bytes;
- end-of-sample thread count;
- process start timestamp and display image name;
- whether the stable row is FileOp's own capture process.

Visible rows are ordered by processor-time delta, then working set, private memory, PID, and process start time. The visible-row budget does not discard hidden CPU evidence: FileOp retains the hidden stable-instance count and their aggregate processor-time delta.

The report also retains start/end enumeration counts, inaccessible reads, snapshot-cap evidence, stable matched instances, stable readable identities that appeared/disappeared between frames, and FileOp's own CPU-delta/working-set observer evidence when readable.

## Sampling and observer overhead

The default sampling delay is one second, bounded to 250 ms–3 s. Each frame has an explicit process-snapshot cap and the report has an explicit visible-row cap.

The Windows provider takes exactly two snapshots around one `Task.Delay`. It measures provider elapsed time with `Stopwatch` and reports time beyond the requested sampling delay as provider overhead. It does not use a periodic timer or continuous sampler.

CPU percentage is intentionally **not derived** in this slice. Individual process counters are read sequentially rather than at one simultaneous instant, so dividing every CPU delta by one nominal wall interval would imply precision the capture does not have.

## Startup/background interpretation boundary

Process start time is context, not proof of startup impact. This bounded current sample does not reconstruct historical CPU or memory use at boot.

FileOp also does not inspect startup registry keys, Startup folders, or startup-task declarations and label their presence as a performance problem. A later UI may show currently measured machine activity alongside stable start-time context, but any startup/background conclusion must remain grounded in measured resource evidence rather than registration presence alone.

## Partial evidence

The report is explicitly incomplete when either frame reaches the process-snapshot cap or required counters are inaccessible for one or more enumerated processes.

Appeared/disappeared counts cover only stable readable identities present in the captured frames; they are not machine-wide process creation/exit event counts. Protected or capped processes can therefore remain outside CPU-delta attribution without disappearing silently from evidence quality.

## Scope boundary

This slice does not:

- classify foreground/background process state;
- enumerate startup registry keys or Startup folders;
- inspect or mutate startup-app enablement;
- stop, suspend, kill, reprioritize, restart, or otherwise manage processes;
- disable services or scheduled tasks;
- persist process activity history;
- add a timer, watcher, or resident sampler;
- create an impact/health score;
- infer CPU percentage from the approximate frame interval;
- add an indexing-helper protocol operation.

The provider is read-only. Protocol remains v8, and any later management action requires a separately reviewed authorization/recovery boundary.
