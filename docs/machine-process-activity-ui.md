# Explicit current machine-process activity UI

The Performance surface can now run the bounded machine-process activity evidence from `machine-process-activity.md` as a **separate explicit action**.

It is not part of the ordinary Performance diagnostics refresh, does not run when Storage analysis loads, and is not a timer/background sampler.

## Explicit capture lifecycle

`MachineProcessActivityView` exposes one `Capture machine activity` button. The button routes to a MainWindow-owned async method, which calls `DesktopSearchEngine.CaptureMachineProcessActivityAsync()` with `MachineProcessActivityBudget.Default` and the engine lifetime cancellation token.

The engine owns one `WindowsMachineProcessActivityProvider`. The existing #104 provider still takes exactly two process-counter frames around the bounded delay; the UI does not add another frame, poller or counter source.

The MainWindow path reuses the existing explicit performance-capture exclusion flag. While the one-second machine sample runs:

- Disk I/O capture cannot start;
- same-size content verification cannot start;
- Storage/Performance refresh controls are disabled;
- index-state changes cannot re-enable the existing Disk I/O capture path behind the active sample.

The exclusion flag is released in `finally`, including failure/cancellation paths.

System-wide machine activity, like Disk I/O capture, does not depend on native Storage optimization/index readiness. It is scoped only to the visible Optimize/Performance surface and the existing window lifetime.

## Presented evidence

The card shows only #104's measured evidence:

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

CPU percentage is intentionally not derived. The provider reads individual process counters sequentially, so the UI displays the measured CPU-time delta instead of dividing by one nominal wall interval.

## Startup/background interpretation boundary

The heading is `Current machine process activity`, not `Background processes` or `Startup impact`.

Process start time is displayed only as stable identity/context. FileOp does not claim that a process started with Windows, delayed boot, is a background process, or should be disabled merely because of its start timestamp or current resource use.

This UI does not enumerate Run keys, Startup folders, startup tasks or service configuration. A future startup-impact conclusion still requires evidence that actually measures startup impact rather than registration presence.

## Failure and observer boundaries

Unsupported/unavailable machine activity clears the machine rows and leaves other Performance, Storage and Disk I/O evidence intact.

A machine-activity exception updates only this card. Window lifetime cancellation is swallowed by the MainWindow capture path as part of shutdown.

The FileOp observer row/evidence is shown explicitly rather than subtracted from machine totals or hidden.

## Scope boundary

This slice does not:

- add a periodic process sampler, timer, watcher or persistence;
- calculate CPU percentage;
- classify foreground/background process state;
- infer startup/boot impact;
- enumerate or mutate startup registrations;
- stop, suspend, kill, reprioritize or restart processes;
- disable services or tasks;
- create an impact/health/performance score;
- alter Disk I/O ranking or device evidence;
- add an indexing-helper operation or protocol change.

Protocol remains v8.

## Validation

`tools/verify_machine_process_activity_ui.py` contains a randomized presentation/lifecycle model plus XAML/source guards. The directly gated `verify_machine_process_activity.py` runs the UI child model/source checker transitively, so `tools/test-local.ps1 -OfflineOnly` covers both #104's provider contract and this UI integration.

Native Windows/.NET/WinUI execution remains required before release claims about the actual one-second capture and rendered control.
