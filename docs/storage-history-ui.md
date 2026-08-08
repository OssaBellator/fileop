# Storage History UI

## Scope

The first desktop history surface tracks the whole primary native NTFS volume. It does not automatically create a time series for every directory visited in Storage, and it does not mix crawler-fallback snapshots into the durable native series.

The History surface is read-only. There is no manual backdated capture, force-capture button, retention deletion or forecasting control.

## Scheduling

`DesktopSearchEngine` owns automatic capture scheduling independently of whether the Storage page has been opened.

A capture is eligible only when the primary native index is current. Background synchronization reports current state before releasing the native operation gate, so the scheduler yields briefly after the state notification and then attempts both desktop foreground/native gates without waiting.

This keeps automatic history below foreground work:

- Search/Storage already in progress wins immediately;
- desktop contention defers the capture instead of queueing it;
- service/SQLite `Busy` is retryable and uses a longer cooldown;
- snapshot/elevation/volume availability failures do not create history;
- a successful UTC-hour bucket suppresses more automatic captures for that bucket in the same desktop session.

The service still owns the authoritative UTC bucket, so retries and app restarts remain idempotent in SQLite.

Once a capture request has been transmitted, FileOp does not cancel it merely because new foreground work appeared. Interrupting an active request would fault the single named-pipe session under the existing IPC rule.

## Whole-volume policy

Automatic capture uses:

```text
VolumeRootPath = primary native volume root
DirectoryPath  = primary native volume root
```

Protocol v5 can store another directory root, but implicit per-folder tracking is intentionally absent. A future explicit folder-history feature can choose roots deliberately and expose their storage cost to the user.

## History mode

Storage has three modes:

```text
Folders | Types | History
```

`StorageHistoryView` is a standalone WinUI UserControl. `MainWindow.StorageHistory.cs` creates it after `MainWindow.InitializeComponent` and inserts it into the existing Storage content grid. The History mode button is inserted beside Folders and Types at the same time.

This keeps the large reviewed `MainWindow.xaml` unchanged while giving History its own XAML templates and presentation state.

The view is native-only. When FileOp is in fallback mode it explains that the static profile snapshot is not part of the native historical series.

## Timeline semantics

The query loads at most 90 observations by default and renders them chronologically.

The entire visible timeline uses one unit:

- physical allocation if every displayed observation has exact allocation;
- otherwise logical size for every observation.

Each row shows local display time, proportional size relative to the largest displayed point, formatted size and signed change from the previous point. The first observation is the baseline.

A loaded-empty result is cached as a valid result. Repeating native synchronization status notifications therefore does not repeatedly query an empty history table.

## What grew?

The right-hand panel compares the newest two observations with `StorageHistoryDelta.Between`.

When both root allocations are known, category deltas use physical allocation. Otherwise the whole newest-pair comparison uses logical bytes. Category rows also show signed file-name, hard-link-alias and extension-group count changes.

Negative values are preserved as shrinkage. The panel is an explanation of the latest interval, not a forecast or a health score.

## UI and IPC lifecycle

History queries are foreground operations. They use the existing desktop search/native gates and single helper session, so they can serialize behind an in-progress native operation even though the service does not require a current checkpoint to read persisted history.

UI generations discard stale queued/completed results without cancelling transmitted IPC. A separate active-load generation prevents recurring engine state notifications from stopping the History progress indicator or re-enabling Refresh while a query is still active.

A successful automatic capture emits `StorageHistoryCaptured`. If History is visible, its cache is invalidated and reloaded; otherwise it remains stale until the next visit.

## Validation without hosted Actions

`tools/verify_storage_history_ui.py` uses Python's standard library to guard:

- UTC-hour due/cooldown behavior;
- engine-owned scheduling independent of opening Storage;
- the post-sync yield before a non-blocking capture attempt;
- non-blocking automatic acquisition of both desktop gates;
- normal serialized gate use for foreground history queries;
- whole-primary-volume capture scope;
- native-only/fallback exclusion;
- single-unit timeline selection;
- latest-pair delta wiring;
- loaded-empty caching and active-load state;
- standalone UserControl XAML parsing and event-handler resolution;
- dynamic insertion while `MainWindow.xaml` remains untouched.

The pure scheduler model was exercised over 100,000 randomized timezone/cadence cases in the review sandbox.

`tools/test-local.ps1` runs this verifier before the full Core/native/indexer/test/WinUI/bundled-helper Windows gate. The review sandbox still has no .NET/Windows toolchain, so these static/property checks are not represented as a successful WinUI compile.
