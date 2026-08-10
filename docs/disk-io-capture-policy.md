# Disk-I/O capture policy

## Purpose

`DiskIoAttributionAnalyzer` defines what disk-specific owner evidence means. Before FileOp implements the Windows ETW collector, the provider also needs a strict workload/result contract so tracing cannot silently become an unbounded background profiler.

This slice adds that contract. It still does **not** start, control or consume an ETW session.

## Initial capture budget

`DiskIoCaptureBudget` is intentionally short and bounded:

| Limit | Default | Hard contract limit |
|---|---:|---:|
| Observation duration | 2 seconds | 5 seconds |
| Normalized disk events | 100,000 | 500,000 |
| Visible owners per physical disk | 12 | 100 |

The minimum duration is 250 ms.

These values are workload-safety policy, not statistical guarantees. Reaching the observation cap means the result is explicitly incomplete; FileOp must not claim that a capped trace fully represents the requested duration.

A later provider may justify changing the defaults only with measured event rates, memory use and tracing overhead. The hard limits should remain independently reviewable.

## Provider interface

`IDiskIoAttributionProvider.CaptureAsync` accepts exactly:

```text
DiskIoCaptureBudget
CancellationToken
```

Cancellation is a caller-controlled operation boundary. A Windows provider must stop/clean up its own trace resources and propagate cancellation rather than translating it into a successful empty capture.

No recurring cadence is part of this interface. Scheduling a repeated capture would require a separate product policy review.

## Result states

`DiskIoCaptureStatus` distinguishes:

- `Completed` — a bounded attribution report exists;
- `Unsupported` — the provider/platform cannot supply the required disk-specific evidence;
- `PermissionRequired` — the required trace control is not permitted in the current security context;
- `SessionUnavailable` — required system-logger/session capacity or coexistence is unavailable.

Unexpected provider failures are not represented as a fake status row; implementations may throw an ordinary provider exception for unexpected faults.

Unavailable states cannot carry a report or a stop reason. Their event-loss state remains `Unknown` because no successful capture exists to assess.

## Stop reason

Every completed capture must state why it stopped:

- `DurationElapsed` — the requested observation window completed;
- `ObservationLimitReached` — the normalized-event cap ended collection first.

`ObservationLimitReached` is valid only when the report contains exactly the budget's maximum accepted observation count.

The report must also obey the requested owner-row limit and must not describe an observation window longer than the budget.

## Event-loss evidence

Every completed result has one `DiskIoCaptureLossState`:

- `NoneObserved` with explicit lost-event count `0`;
- `Observed` with a positive lost-event count;
- `Unknown` with no invented count.

`EvidenceMayBeIncomplete` is true when:

- the observation limit stopped capture; or
- event loss was observed; or
- event-loss status is unknown.

This flag is a disclosure property, not a quality/health score. It does not say how wrong the evidence is and cannot turn incomplete evidence into a recommendation.

## Provider overhead

A provider may return `ProviderOverheadDuration` as a non-negative wall-clock observation of its own setup/teardown/processing work outside the intended evidence window.

This is not CPU utilization and is not the same as ETW's system-wide tracing cost. The eventual Windows provider still needs a measured implementation-specific overhead study before FileOp chooses any repeated sampling policy.

## Windows session boundary

Microsoft documents additional restrictions for system/kernel trace sessions, including permission requirements and limits on simultaneously active system loggers. The future Windows implementation must map expected access/session-capacity failures into `PermissionRequired` or `SessionUnavailable` without modifying system policy to make the trace work.

In particular FileOp must not:

- add the user to a privileged group automatically;
- modify ETW logger-count registry limits;
- stop another application's trace session merely to obtain a slot;
- reuse another session without explicit ownership/compatibility evidence;
- start a permanent AutoLogger/background session for this feature.

## Result integrity

A completed result is rejected when its report contradicts the budget:

- report duration exceeds requested duration;
- accepted observations exceed the cap;
- owner-row limit differs from the request;
- observation-cap stop reason is claimed before the cap was reached;
- event-loss state and lost-event count disagree.

Unavailable results are rejected if they try to carry a report/stop reason or invent event-loss evidence.

Provider detail text is required and non-blank so UI can explain the exact boundary instead of showing an opaque unavailable state.

## Deliberate non-goals

This policy slice does not:

- P/Invoke ETW APIs;
- select buffer sizes;
- choose a system-logger GUID/name;
- request elevation;
- create an indexing-service protocol operation;
- add a UI button;
- persist captures;
- start timers or background loops;
- infer bottleneck severity from transfer bytes alone.

The next Windows-provider PR must remain separately reviewable because it crosses a privilege/session/resource boundary that the current indexing helper does not own.

## Validation without GitHub Actions

`tools/verify_disk_io_capture_policy.py` performs randomized budget/result property checks and repository guards for:

- duration/event/owner hard limits;
- completed versus unavailable result shape;
- report/budget consistency;
- observation-limit semantics;
- event-loss state/count consistency;
- incomplete-evidence disclosure;
- non-negative provider-overhead evidence;
- required cancellation-token provider interface;
- absence of ETW/session-control, timer, elevation, registry or process-control implementation in this policy-only slice.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. Focused .NET regressions cover the same contract when the local Windows/.NET gate is available.
