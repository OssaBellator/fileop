# Explicit startup application degradation history UI

The Performance surface can now read the bounded historical compatibility evidence from `startup-application-degradation.md` through a separate **Read startup degradation history** action.

This action is not part of ordinary `Refresh diagnostics`, Storage analysis loading, Disk I/O capture, machine-process activity or fragmentation analysis. It never runs automatically and it does not install an event-log subscription or watcher.

## Worker and lifetime boundary

`WindowsStartupApplicationDegradationProvider` is a synchronous EventLog reader. `DesktopSearchEngine.CaptureStartupApplicationDegradationAsync()` therefore runs one provider query on a worker task with the engine/window lifetime cancellation token.

The worker query uses `StartupApplicationDegradationBudget.Default`: at most 20 visible Event 101 compatibility records within the provider's 2-second total read budget. The UI does not add another EventLog query, polling loop or hidden retry.

## Explicit diagnostic exclusion

Startup-history capture reuses the existing MainWindow explicit-performance exclusion flag. When a startup-history read starts:

- normal Storage/Performance refresh is disabled;
- Disk I/O capture cannot start;
- current machine-process activity cannot start;
- volume fragmentation analysis cannot start;
- same-size content verification cannot start;
- the startup-history action itself is disabled.

The exclusion flag is released in `finally`.

Machine activity and fragmentation analysis explicitly disable/restore the startup-history action while their own samples run. Disk I/O and same-size verification already reject overlap through the same MainWindow flag. If startup history is requested while one of those guarded diagnostics is active, FileOp displays a busy notice and performs **no EventLog query**.

That busy notice is intentionally state-neutral: it updates only the startup-history card and does not clear the same-size verification control block or change the active diagnostic's state.

## System-wide / index independence

Historical Diagnostics-Performance events are machine-level Windows evidence. Startup-history capture does **not** require `StorageOptimizationAvailable`, native indexing, an NTFS source, or a drive-root derivation.

The card is hosted on the existing Optimize/Performance surface, but native/fallback Storage source readiness is not used as evidence that the event history can or cannot be read.

## Presented evidence

Completed results are rendered in the exact newest-first order already supplied by #111. The UI does **not** re-sort, truncate or promote rows by duration.

Each visible row shows:

- Windows component identity (`FriendlyName` when available plus raw `Name`);
- optional component version;
- event recorded timestamp;
- Windows-reported incident/start timestamp;
- raw Windows `TotalTime` milliseconds;
- raw Windows `DegradationTime` milliseconds;
- exact event-log record ID plus optional event schema version.

The status text preserves query elapsed time and #111's `MoreMatchingEventsAvailable` evidence. If the extra peek found an older match beyond the visible budget, FileOp says so explicitly.

The UI does not calculate a degradation percentage, threshold/severity, average, total score or "startup impact" ranking from those raw durations. An Event 101 row is presented as a retained Windows compatibility event, not as a FileOp recommendation to disable a component.

## Empty and unavailable evidence

The #111 provider already states that zero retained Event 101 records are **not proof that startup was fast** or that no startup application/component had impact. The UI preserves that wording.

Unsupported, permission-required, cancelled and unavailable results clear only the historical rows and show their state, elapsed time and detail. They are not converted into a green/healthy startup result.

## Relationship to current process activity

The existing `Current machine process activity` card (#104/#108) measures current CPU-time deltas and end-of-sample memory/thread counters for stable PID + process-start identities.

Historical Event 101 rows are separate Windows event-log records. This UI does not join component names to current PIDs, does not claim a current process caused a historical event, and does not re-rank one evidence stream using the other.

A user can inspect both streams, but FileOp keeps their provenance explicit.

## Registration and control boundary

This UI does not enumerate or mutate:

- Run/RunOnce registry keys;
- Startup folders;
- packaged startup tasks;
- services or scheduled tasks;
- process priority, affinity or lifetime.

No `Disable`, `Stop`, `Kill`, `Suspend`, `Remove from startup`, `Fix` or equivalent action is added.

Registration presence remains outside this measured-impact slice.

## Scope boundary

This slice adds no:

- event-log watcher/subscription;
- periodic timer or background polling;
- persisted startup history;
- duration-derived score, grade or severity threshold;
- process/current-PID join heuristic;
- startup registration enumeration;
- service/task/process control;
- indexing-helper operation or protocol change.

Protocol remains v8.

## Validation

`tools/verify_startup_application_degradation_ui.py` models explicit-capture eligibility, native-index independence, busy-click state neutrality, newest-first presentation, raw-duration preservation and truthful hidden-event evidence. It also parses the Performance/startup XAML and source-guards the worker/lifetime/exclusion wiring and no-action boundaries.

The startup-history verifier and directly gated machine-activity verifier both run the UI child model/source checker transitively. Native Windows/.NET/WinUI/EventLog execution remains required before release claims about live channel availability, cancellation or rendering.
