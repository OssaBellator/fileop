# Bounded Windows system CPU activity evidence

FileOp can capture one explicit, bounded Windows CPU-activity interval using `GetSystemTimes`. This is machine-level timing context for #62. It is not a CPU health/pressure score and it does not convert FileOp's existing per-process counters into CPU percentages.

## Windows counter semantics

`GetSystemTimes` returns cumulative idle, kernel and user processor time in 100-nanosecond units.

Windows documents an important relationship: **kernel time includes idle time**. FileOp therefore derives one interval as:

- `idle delta = end idle - start idle`;
- `kernel delta = end kernel - start kernel`;
- `user delta = end user - start user`;
- `total processor-time delta = kernel delta + user delta`;
- `busy processor-time delta = total delta - idle delta`;
- `busy percent = busy delta / total delta * 100`, only when total delta is nonzero.

A zero total delta produces no percentage rather than an invented `0%` value.

All cumulative counters must be monotonic during the sample, and idle delta must not exceed the kernel delta that contains it. Invalid relationships fail closed as unavailable evidence. The arithmetic uses overflow-safe `UInt128` addition before converting the bounded interval to `TimeSpan` ticks; .NET `TimeSpan` ticks are also 100 nanoseconds.

## Processor-group scope

Microsoft documents `GetSystemTimes` as summing all processors on systems with 64 or fewer processors. On systems with more than 64 logical processors, the returned values can be limited to the caller thread's **primary processor group**.

FileOp therefore does not universally label this evidence `whole-machine CPU` on every topology. Provider detail preserves the processor-group caveat. A later UI should use wording such as Windows system/processor-group CPU interval evidence rather than claiming universal whole-machine coverage.

## Bounded provider lifecycle

`SystemCpuActivityBudget` defaults to one second and accepts only 250 ms through 3 seconds.

`WindowsSystemCpuActivityProvider` performs exactly:

1. one `GetSystemTimes` observation;
2. one cancellable bounded delay;
3. one second `GetSystemTimes` observation.

The provider serializes its own captures with a semaphore. Caller cancellation propagates. If the first native query fails, FileOp returns unavailable evidence immediately and does not start the delay. If the second query fails, the already-performed delay is disclosed by the provider result but no CPU interval is invented.

The provider measures Stopwatch elapsed time and reports provider overhead beyond the requested delay. There is no timer, watcher, periodic sampler or detached task.

## Relationship to measured process activity

#104/#108 already provide one explicit bounded current-process activity capture using stable PID + process-start identity, per-process total-processor-time deltas, end working/private memory and thread counts.

This `GetSystemTimes` evidence is a **separate evidence stream**. It is not a denominator for those rows because the process counters are read sequentially across two process-enumeration frames, while `GetSystemTimes` is a system counter sampled at two distinct instants. FileOp does not claim that visible/hidden process rows sum to system busy time or that any residual represents an unknown process.

A later presentation slice may run both bounded captures under the same explicit user action and show their intervals side-by-side as context. It must preserve their separate timestamps, scopes and overhead and must not fabricate per-process CPU percentages from them.

## Interpretation boundary

A high busy percentage during one bounded interval means only that Windows reported a large busy share of the sampled processor time for that interval/scope. It is not by itself:

- proof of sustained CPU pressure;
- a slow-PC diagnosis;
- a thermal/power problem;
- a reason to disable a service or startup application;
- a reason to change process priority/affinity;
- evidence that a particular process caused all observed busy time.

FileOp adds no universal busy-percent threshold, green/yellow/red grade, CPU health score, pressure score or recommendation in this slice.

## Scope boundary

This foundation adds no:

- UI or automatic capture;
- per-process CPU percentage;
- process/service stop, suspend, priority or affinity control;
- Performance Counter/WMI/ETW CPU sampler;
- timer, watcher or background monitor;
- persistence/history;
- power-plan or scheduler mutation;
- indexing-helper operation or protocol change.

Protocol remains v8.

## Validation boundary

Portable verification covers the exact Windows arithmetic, zero-total behavior, cumulative-counter regression, impossible idle/kernel relationships, independent base-counter offsets and valid busy percentages across randomized intervals.

Source guards pin the two-query/one-delay lifecycle, `GetSystemTimes` P/Invoke, 100-nanosecond counter representation, kernel-includes-idle formula, `UInt128` overflow boundary, cancellation/overhead evidence, processor-group documentation and the absence of CPU thresholds/scores/process controls/pollers.

Focused .NET regressions additionally cover budget limits, provider query/delay counts, first/second native failures, inconsistent intervals, cancellation and a conservative Windows-native bounded smoke capture. Native Windows/.NET execution remains part of the normal local release-validation path.
