# System physical-memory evidence on Performance refresh

The Performance surface now presents #113's machine-level physical-memory snapshot whenever the existing **Refresh diagnostics** action is invoked.

There is no second memory-specific capture button and no background sampler. The card attaches to the existing `PerformanceDiagnosticsView` visual tree in `OnApplyTemplate` and subscribes once to the view's existing `RefreshRequested` event.

## Worker and provider boundary

`DesktopSearchEngine` owns one `WindowsSystemPhysicalMemoryStatusProvider`. `MainWindow.CapturePerformanceSystemPhysicalMemoryStatusAsync()` runs the one synchronous provider query on a worker task using the window lifetime cancellation token.

This work is supplementary to the existing Performance refresh. It does not alter the existing `PerformanceDiagnosticsSnapshot`, latency-history samples, index-database metrics or Storage probe contract.

The provider still issues exactly one `GlobalMemoryStatusEx` call. The UI adds no second native query, retry loop or polling mechanism.

## Presented evidence

The card shows:

- total physical memory;
- currently available physical memory;
- used physical bytes (`total - available`);
- **Windows load (approx.)** from `dwMemoryLoad`;
- provider query elapsed time and availability/detail text.

The Windows load percentage is displayed exactly as preserved by #113. The UI does not recompute it from the byte counters and does not flag a mismatch between the approximate percentage and a byte-derived ratio.

The card explicitly says that available memory is not automatically wasted/recoverable RAM and the approximate load value is not a FileOp memory-health or cleanup verdict.

## Refresh lifecycle

`OnApplyTemplate` can run more than once during a control lifetime, so the partial view keeps separate `panel attached` and `refresh subscribed` flags. It appends the card once and subscribes to `RefreshRequested` once.

A small in-flight flag prevents duplicate physical-memory queries if the refresh event is raised again before the previous worker call finishes.

When a normal Performance refresh is unavailable or disabled by another explicit diagnostic, the existing Refresh button cannot initiate this query. No independent timer or watcher bypasses the existing UI readiness model.

## Relationship to other evidence

#104/#108 current machine-process activity remains the per-process CPU/working/private-memory evidence stream. This system card does not:

- rank or filter process rows;
- subtract visible process working sets from system memory;
- label unaccounted bytes as a leak;
- join historical startup events to current processes;
- alter Disk I/O or fragmentation evidence.

The physical-memory snapshot is one machine-level point-in-time context value displayed alongside, not merged into, those other evidence types.

## Mutation boundary

This UI does not call or offer:

- `EmptyWorkingSet`;
- `SetProcessWorkingSetSize`;
- `GC.Collect` as a cleanup action;
- process stop/suspend/kill/reprioritization;
- a RAM cleaner, cache clear or memory optimizer;
- a free-memory/load threshold, score or severity grade;
- a timer, watcher or persisted memory history;
- an indexing-helper operation or protocol change.

Protocol remains v8.

## Validation

`tools/verify_system_physical_memory_status_ui.py` models refresh-trigger deduplication and evidence-preserving presentation, and source-guards the `OnApplyTemplate` attachment/subscription, worker/lifetime bridge, existing-refresh trigger, no-recompute wording and no-action boundaries.

The #113 memory verifier imports and runs the UI child model/source checker transitively. Because the already-gated Performance verifier invokes the memory verifier, `tools/test-local.ps1 -OfflineOnly` covers the full provider + UI chain without another PowerShell edit.

Native Windows/.NET/WinUI execution remains required before release claims about rendered layout, live `GlobalMemoryStatusEx` values or actual query cost.
