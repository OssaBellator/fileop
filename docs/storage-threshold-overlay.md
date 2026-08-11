# Storage Optimize view thresholds

## Purpose

Storage Optimize's native helper intentionally returns a bounded read-only analysis using the thresholds captured in `StorageOptimizationPolicy`.

This slice adds **session-only stricter view thresholds** so a user can focus the already-returned evidence without rerunning the helper or widening its query.

The controls adjust:

- large-file minimum size;
- same-size candidate minimum size;
- stale-age minimum.

## Completeness boundary

The overlay can only make the native result **narrower**.

`StorageOptimizationThresholdFilter.ValidateAgainstAnalysis` rejects:

- a large-file threshold below `analysis.Policy.LargeFileMinimumBytes`;
- a same-size threshold below `analysis.Policy.SameSizeMinimumBytes`;
- a stale-age value younger than `analysis.Policy.StaleAgeDays`.

The reason is evidence completeness. A client-side filter cannot reconstruct files/groups that the helper never returned. FileOp therefore refuses to present a looser view as if it were complete.

Lower/younger thresholds would require a separately reviewed typed helper-policy transport rather than a UI-only illusion.

## Presets

The panel derives its presets from the helper baseline instead of hard-coding assumptions about the current defaults.

Size choices include the baseline and approximately 2×, 4× and 8× stricter values. Stale-age choices include the baseline and 2×, 4× and 6× older values. Multiplication saturates instead of overflowing.

The current selection is retained for the life of the view when it is still compatible with a refreshed analysis. It is not persisted across application launches in this slice.

## No extra workload

Changing a view threshold does not:

- call `AnalyzeStorageOptimizationAsync` again;
- send a new indexing-service request;
- read file contents;
- run SHA-256;
- start ETW;
- scan the filesystem;
- create a timer/background task.

It only filters the current in-memory `StorageOptimizationAnalysis`.

Protocol remains v8.

## Existing verification evidence

Same-size rows retain their original analysis group indices.

When a stricter threshold hides a group and the user later shows it again, any existing in-memory SHA-256/current-physical verification result for that original group index remains attached to the correct row. The overlay does not copy or reinterpret the verification evidence.

Threshold controls are disabled while same-size content verification is active, preventing a running verification row from being reshaped mid-operation.

## Explicit view-state integration

The threshold layer does not observe `ItemsSource` asynchronously.

`StorageOptimizationView` installs the threshold panel immediately after `InitializeComponent` and calls explicit threshold hooks from the existing view lifecycle:

- full Optimize `SetLoading` marks the threshold layer analysis-loading before the loading message is displayed;
- `Apply` clears that state and applies the filtered view synchronously;
- `RefreshSameSizeRows` asks the threshold layer to render filtered rows while preserving original group indices;
- `SetUnavailable` suppresses threshold rendering while cached analysis is cleared.

This avoids repainting cached “Showing…” status text over a legitimate full-analysis loading/unavailable state.

Performance/Disk-I/O/content-verification state changes still reuse the explicit same-size row hook, so verification buttons and threshold controls remain coordinated without rerunning Optimize.

## Displayed status

The panel shows both:

- the native helper baseline thresholds; and
- the current stricter view thresholds.

The main Optimize status is recomputed from the filtered rows and labels the same-size total as the **displayed logical candidate upper bound**.

The UI explicitly states that the controls only narrow an already bounded result and do not imply evidence below the helper baseline.

## Deliberate non-goals

This slice does not:

- persist settings;
- lower native helper thresholds;
- change result caps;
- add arbitrary free-form numeric input;
- change protocol v8;
- rerun Optimize automatically after a selection;
- alter content/physical verification semantics;
- authorize cleanup;
- create a storage-health or optimisation score.

## Validation without GitHub Actions

`tools/verify_storage_threshold_overlay.py` models baseline preservation, rejection of looser settings, deterministic stricter filtering and saturated candidate-byte totals across randomized bounded analyses.

Its source guards require the explicit loading-state integration, forbid the discarded dependency-property callback/queued-refresh approach, require preservation of original group indices, require unchanged protocol v8 and confirm the verifier is part of `tools/test-local.ps1 -OfflineOnly`.

Focused .NET tests cover baseline identity, stricter filtering/order preservation, rejection of all three looser dimensions and saturated same-size totals.

Native WinUI/.NET execution is not claimed from the current sandbox.
