# System CPU interval context in machine activity

FileOp presents the bounded `GetSystemTimes` evidence from `system-cpu-activity.md` inside the existing explicit **Capture machine activity** action. It does not add a second CPU button or a background sampler.

## One action, two separate bounded evidence streams

`DesktopSearchEngine.CaptureMachineProcessActivityAsync()` starts both captures before awaiting either result:

- the existing #104/#108 machine-process provider;
- one `WindowsSystemCpuActivityProvider` using `SystemCpuActivityBudget.Default`.

`Task.WhenAll` waits for both bounded operations. Both default to one second, so the action does not intentionally serialize two one-second samples into a two-second workflow.

The intervals are still **not identical measurement boundaries**. Process counters are read sequentially while each process frame is enumerated; `GetSystemTimes` has its own two native observation instants. The UI preserves them as separate evidence streams and does not divide process CPU-time deltas by the system CPU interval.

Caller/window lifetime cancellation propagates through both captures. An unexpected supplementary CPU-provider exception is converted to an explicit unavailable CPU result; it does not erase a valid process report. Process capture keeps its existing primary-error behavior.

The existing MainWindow explicit-diagnostics exclusion is unchanged, so the combined machine action still cannot overlap Disk I/O capture, fragmentation analysis, startup-history reads, same-size verification or normal diagnostics refresh.

## Presentation

The existing `Current machine process activity` card gains a separate **Windows system CPU interval** strip above the process summary/table.

For completed system CPU evidence it shows:

- derived busy share for that exact `GetSystemTimes` interval;
- busy / total processor-time delta and idle processor-time delta;
- observed wall interval plus start/end timestamps;
- provider overhead beyond the requested delay;
- the provider detail, including the >64-logical-processor primary-processor-group caveat.

When total processor-time delta is zero, the UI shows **No percentage** rather than inventing `0%`.

Unsupported/unavailable CPU context leaves the process rows intact and shows the CPU status/detail separately. Likewise, a completed CPU interval can remain visible when the process result itself is unavailable.

The process table keeps the exact #104/#108 ordering and fields. There is no new sort, truncation, promotion or per-process percentage.

## Interpretation boundary

The CPU strip is interval context only. FileOp does not claim that:

- one busy-share value proves sustained CPU pressure;
- process rows sum to the system busy value;
- a difference between process deltas and system busy time is an unknown process or leak;
- the highest CPU-time-delta process caused all system busy time;
- the result is universally whole-machine on >64-logical-processor systems;
- a threshold has been crossed or a process/service should be disabled.

No green/yellow/red state, CPU pressure/health score, threshold, priority/affinity control or process/service action is added.

## Validation boundary

`verify_system_cpu_activity_ui.py` models independent process and CPU result states. It requires process ordering to remain unchanged under arbitrary CPU busy-value changes, valid process evidence to survive CPU unavailability, valid CPU evidence to survive process unavailability, and system percentage/timestamps/overhead to be presented independently from process rows.

Source/XAML guards require both captures to start before `Task.WhenAll`, preserve the existing MainWindow exclusion lifecycle, render `SystemCpuActivityResult` separately, display the processor-group/provider detail, and forbid process-percentage division, CPU thresholds/scores, extra capture buttons, polling and ranking changes.

`verify_system_cpu_activity.py` exposes a combined provider+UI model/source-check path, and the directly gated machine verifier consumes that combined API through the existing `verify_background_process_activity.py` PowerShell entry.

Native Windows/.NET/WinUI execution remains part of the normal local release-validation path.
