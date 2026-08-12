# Explicit current machine-process activity UI

The Performance surface can run the bounded machine-process activity evidence from `machine-process-activity.md` as a **separate explicit action**. The same click also captures the bounded Windows system CPU interval from `system-cpu-activity.md` as supplementary context.

It is not part of the ordinary Performance diagnostics refresh, does not run when Storage analysis loads, and is not a timer/background sampler.

## Explicit capture lifecycle

`MachineProcessActivityView` exposes one `Capture machine activity` button. The button routes to a MainWindow-owned async method, which calls `DesktopSearchEngine.CaptureMachineProcessActivityAsync()` with the engine lifetime cancellation token.

The engine owns one `WindowsMachineProcessActivityProvider` and one `WindowsSystemCpuActivityProvider`. It starts both bounded captures concurrently and awaits both. The existing process provider still takes exactly two process-counter frames around its bounded delay. The system CPU provider independently takes exactly two `GetSystemTimes` observations around its own bounded delay. The UI adds no extra frame, poller or counter source.

The two streams deliberately keep separate observation boundaries. Process counters are read sequentially across process frames, while `GetSystemTimes` is sampled at two native instants. FileOp therefore does not divide process CPU-time deltas by the system CPU interval or claim that visible process rows decompose Windows busy CPU time.

System CPU capture is supplementary. If that provider returns Unsupported/Unavailable, a valid process report still renders. An unexpected non-cancellation exception from the supplementary CPU provider is converted to unavailable CPU context; engine lifetime cancellation still propagates through the combined explicit action.

The MainWindow path reuses the existing explicit performance-capture exclusion flag. While the machine sample runs:

- Disk I/O capture cannot start;
- same-size content verification cannot start;
- fragmentation and startup-history capture cannot start;
- Storage/Performance refresh controls are disabled;
- index-state changes cannot re-enable another explicit capture behind the active sample.

The exclusion flag is released in `finally`, including failure/cancellation paths.

System-wide activity does not depend on native Storage optimization/index readiness. It is scoped only to the visible Optimize/Performance surface and the existing window lifetime.

## Presented process evidence

The process portion shows only #104's measured evidence:

- stable matched process-instance count and visible-row count;
- readable identities that appeared/disappeared between the two frames;
- aggregate CPU-time delta retained for hidden stable matched rows;
- FileOp observer CPU-time delta and working set when readable;
- visible rows in the exact order already produced by the Core analyzer;
- process image name/PID and FileOp observer marker;
- measured processor-time delta;
- end-of-sample working set;
- end-of-sample private memory;
- end-of-sample thread count;
- stable process start timestamp as context.

The UI does **not** re-sort or promote rows. #104's order remains processor-time delta, working set, private memory and stable identity.

The report status preserves requested sampling delay, appeared/disappeared counts, inaccessible reads, snapshot-cap evidence and provider overhead. If cap/access evidence is present, the card says the evidence may be incomplete.

Process CPU percentage is intentionally not derived. The provider reads individual process counters sequentially, so the UI displays measured CPU-time deltas instead of dividing by one nominal wall interval or by the separate `GetSystemTimes` result.

## Presented Windows system CPU interval

The CPU strip is explicitly labeled `Windows system CPU interval`. For a completed result it renders:

- the provider-derived busy percentage when total processor-time delta is nonzero;
- raw busy, idle and total processor-time deltas;
- the observed wall duration and both native-observation timestamps;
- measured provider overhead beyond the requested delay;
- the provider detail text, including the Windows caveat that on systems with more than 64 logical processors `GetSystemTimes` can be scoped to the caller thread's primary processor group.

A zero-total interval displays `No percentage`; it does not invent 0%. Unsupported or unavailable CPU evidence clears only the CPU summary and leaves a valid process report intact.

The system CPU percentage is interval context, not a scored assessment of pressure or health and not a per-process percentage. The UI does not apply thresholds, traffic-light labels, recommendations or rankings to it.

The process-only `Apply(MachineProcessActivityResult)` presentation seam remains available for compatibility. It renders the same process evidence and explicitly marks system CPU context as not attached rather than fabricating a value.

## Startup/background interpretation boundary

The heading is `Current machine process activity`, not `Background processes` or `Startup impact`.

Process start time is displayed only as stable identity/context. FileOp does not claim that a process started with Windows, delayed boot, is a background process, or should be disabled merely because of its start timestamp or current resource use.

This UI does not enumerate Run keys, Startup folders, startup tasks or service configuration. A future startup-impact conclusion still requires evidence that actually measures startup impact rather than registration presence.

## Failure and observer boundaries

Unsupported/unavailable process activity clears the machine rows but does not erase separately completed CPU interval context from the same bundle. A top-level machine-activity exception updates only this card. Window lifetime cancellation is swallowed by the MainWindow capture path as part of shutdown.

The FileOp observer row/evidence is shown explicitly rather than subtracted from machine totals or hidden.

## Scope boundary

This slice does not:

- add a periodic process or CPU sampler, timer, watcher or persistence;
- calculate per-process CPU percentage;
- classify foreground/background process state;
- infer startup/boot impact;
- enumerate or mutate startup registrations;
- stop, suspend, kill, reprioritize or restart processes;
- disable services or tasks;
- create a scored impact, health, pressure or performance assessment;
- alter Disk I/O ranking or device evidence;
- add an indexing-helper operation or protocol change.

Protocol remains v8.

## Validation

`tools/verify_machine_process_activity_ui.py` contains a randomized presentation/lifecycle model plus XAML/source guards. The model independently perturbs system CPU interval status/values and requires process row order, hidden CPU aggregation and incomplete-evidence state to remain unchanged. It also requires unavailable CPU context to leave process presentation intact.

The directly gated `verify_machine_process_activity.py` runs the UI child model/source checker transitively, so `tools/test-local.ps1 -OfflineOnly` covers #104's provider contract, the CPU foundation verifier and this combined one-click presentation.

Native Windows/.NET/WinUI execution remains required before release claims about the actual capture and rendered control.
