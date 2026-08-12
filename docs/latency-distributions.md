# Session latency distributions

## Purpose

FileOp's first Performance panel showed one on-demand Search timing and one on-demand Storage timing. A single measurement is useful evidence, but it is too easy to over-interpret as normal performance.

The session latency view therefore keeps a small bounded history of the same exact probes that already run when the user explicitly refreshes Performance diagnostics. It does not add a scheduler, timer, background sampler or persistent telemetry store.

## Sample boundary

Only the existing exact foreground probes contribute samples:

```text
SearchAsync(string.Empty, limit: 1)
AnalyzeStorageAsync(root, maxEntries: 1)
```

Timer-baseline measurements remain visible as current measurements but are excluded from the Search/Storage latency distribution.

Samples are keyed by:

```text
Probe kind
Probe name
Probe scope
```

Native-index and profile-fallback samples therefore remain separate. FileOp does not merge measurements from unlike sources into one distribution. A source-mode or normalized root change starts a fresh window, so evidence from the prior source/root is not carried into the new scope.

## Capacity and lifetime

Each exact probe/scope keeps the most recent **20 explicit diagnostics samples** in memory for the current app session. When a 21st sample arrives, the oldest sample for that probe/scope is discarded. With only the exact Search and Storage probe kinds retained, each active source/root scope holds at most 40 elapsed-time samples.

The history is:

- bounded to 20 samples per probe/scope;
- created only by explicit diagnostics refreshes;
- held in process memory only;
- not persisted to SQLite, files, registry or telemetry;
- not restored after the app exits;
- not populated by a timer or background polling loop.

Switching between sources does not combine their data. The UI only renders distributions represented by the current diagnostics snapshot, so a prior native scope is not shown while fallback is active and vice versa.

## Statistics

The UI always shows the sample count alongside the fixed capacity (`N/20`). A percentile is never shown without its sample count context.

### Minimum and maximum

Minimum and maximum are the smallest and largest non-negative elapsed-microsecond observations in the retained window.

### Median

Samples are ordered by elapsed microseconds.

For an odd sample count, median is the middle observation. For an even sample count, FileOp uses the integer midpoint of the two middle observations:

```text
left + ((right - left) / 2)
```

This intentionally avoids inventing sub-microsecond precision the probe did not record.

### p95

FileOp does not display p95 until at least **5 explicit samples** exist for that exact probe/scope. Before that, the UI says `Collect 5+`.

Once five or more samples exist, FileOp uses nearest-rank p95:

```text
rank = ceil(sample_count × 0.95)
```

with the rank clamped to the retained ordered sample range.

A five-sample p95 is still a very small-sample descriptive statistic. FileOp does not present it as an SLA, quality grade, health score, or prediction of future latency.

## Current measurement versus distribution

The raw probe table remains visible and labels the latest result as **Current elapsed**. The session distribution is a separate table showing:

- probe;
- scope;
- sample count/capacity;
- minimum;
- median;
- p95 when eligible;
- maximum.

This preserves the difference between what happened in the latest explicit refresh and what has happened across the small retained session window.

## No automatic cadence

This feature does not define a sampling interval. FileOp does not attempt to manufacture a statistically representative workload by polling every few seconds or minutes.

That limitation is deliberate. Search and Storage probes consume real foreground/index resources, and a diagnostic tool should not create the load it is trying to explain.

A future low-frequency sampler would require a separate workload/overhead budget, foreground-yield policy, explicit retention semantics and evidence that the resulting distribution answers a real user decision better than explicit session samples.

## Non-goals

The session distribution does not:

- benchmark arbitrary user queries;
- infer disk I/O latency from query latency;
- combine native and fallback sources;
- persist performance telemetry;
- assign green/yellow/red thresholds;
- recommend registry, RAM, service or power-plan changes;
- claim that p95 from a small explicit sample set represents machine-wide performance.

## Validation without GitHub Actions

`tools/verify_latency_distributions.py` models bounded tail-window retention, clamped non-negative timings, integer midpoint medians, nearest-rank p95 eligibility, source separation and timer-baseline exclusion over randomized sample sets.

Repository mode additionally guards the 20-sample and five-sample constants, UI sample-count/p95 wording, legacy exact-probe compatibility, no persistence, no timer/background scheduler and focused .NET regression coverage.

The verifier is part of `tools/test-local.ps1 -OfflineOnly`. The Windows/.NET local gate adds `PerformanceProbeHistoryTests` when the toolchain is available.
