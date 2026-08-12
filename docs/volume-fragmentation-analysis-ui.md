# Explicit volume fragmentation analysis UI

The Performance surface can now request the analysis-only evidence from `volume-fragmentation-analysis.md` through a separate **Analyze fragmentation** action.

This action is not part of ordinary Performance refresh, Storage analysis loading, Disk I/O capture or machine-process sampling. It never runs automatically.

## Volume identity

The current Storage source may be a subdirectory such as a profile fallback path. FileOp derives the containing drive using `Path.GetFullPath` + `Path.GetPathRoot`, then requires #109's canonical local drive-letter root (`X:\`).

The MainWindow snapshots that canonical drive root before analysis begins and passes the same root into the engine. The engine rechecks that the current Storage source still resolves to that drive before the provider is invoked.

After the analysis completes, MainWindow compares the current drive root with the captured root again. If Storage moved to a different drive while WMI was running, FileOp discards the stale result instead of rendering it under the new source.

A source change within the same drive does not invalidate the result because the evidence describes the whole drive volume, not the selected directory.

This first slice remains drive-letter-only. UNC, volume-GUID-only and directory-mount-point identities are not silently remapped.

## Explicit capture exclusion

Fragmentation analysis reuses the same MainWindow explicit-performance exclusion flag already shared by Disk I/O and machine activity. While fragmentation analysis is running:

- normal Storage/Performance refresh is disabled;
- Disk I/O capture cannot start;
- machine-process activity cannot start;
- same-size content verification cannot start;
- the fragmentation action itself is disabled.

The flag is released in `finally`. Machine activity also disables/restores the fragmentation action while its own explicit sample runs.

The fragmentation action does **not** require `StorageOptimizationAvailable`. It can use a fallback Storage source when that source still resolves to an explicit local drive root, because the WMI volume analysis is independent of the native indexing helper.

## Presented evidence

On completed #109 evidence the card displays:

- canonical volume root;
- **Windows legacy recommendation** as `reported yes/no · evidence only`;
- file fragmentation percentage;
- average fragments per file;
- fragmented / total file counts;
- total free-space extent count;
- largest / average free-space extent sizes;
- used / free / volume bytes;
- raw provider return code;
- measured elapsed time and provider detail.

Unsupported, permission-required, cancelled and unavailable results keep their status, raw provider code when present, elapsed time and detail. They are not converted into an optimized/healthy result.

The Windows recommendation is deliberately named as a legacy provider field. The UI does not turn `reported yes` into a FileOp recommendation and does not turn `reported no` into a health or “no maintenance needed” verdict.

## Current/legacy Windows boundary

The structured metrics come from the legacy `Win32_Volume.DefragAnalysis` compatibility provider documented in #109. That WMI surface may be unsupported on current Windows clients.

The card says so before the user starts analysis. FileOp does not silently fall back to parsing `Optimize-Volume -Analyze -Verbose` output, and it does not claim that the newer analyze-only command exposes the same structured payload.

## Relationship to TRIM/device evidence

#105/#107 device TRIM evidence remains a separate physical-device evidence stream surfaced after explicit Disk I/O capture. This UI does not invent a volume-to-physical-disk mapping in order to combine the two.

TRIM capability/status, bus type and seek-penalty evidence do not select, suppress or rank fragmentation results. Likewise, fragmentation evidence does not imply that ReTrim is needed.

## Mutation boundary

This UI adds no action that can change storage. It does not:

- invoke Defrag or ReTrim;
- call `Optimize-Volume` or `defrag.exe`;
- start PowerShell or another child process;
- issue storage/file-system optimization controls;
- create a health, optimization or urgency score;
- schedule analysis or add a timer/watcher;
- add an indexing-helper operation or protocol change.

Protocol remains v8.

## Validation

`tools/verify_volume_fragmentation_analysis_ui.py` models canonical-drive capture eligibility, explicit-capture exclusion, source-stability discard and evidence-preserving presentation. It also parses both XAML files and source-guards the engine/MainWindow/view wiring and no-action boundaries.

The already-gated `verify_volume_fragmentation_analysis.py` imports and runs this UI child model/source checker transitively. Native Windows/.NET/WinUI/WMI execution remains required before release claims about live support, cancellation or rendering.
