# Persisted Optimize view-threshold preferences

FileOp can remember the user's supported **stricter display filters** for Storage Optimize across app sessions without changing the native indexing-helper analysis contract.

This persistence layer stores policy-relative multipliers only. It never persists raw byte or day thresholds.

## Preference format

The local preference is schema version 1 and contains exactly four integer fields:

- `SchemaVersion`;
- `LargeFileMultiplier`;
- `SameSizeMultiplier`;
- `StaleAgeMultiplier`.

Supported values are deliberately identical to the current UI choices:

- large-file multiplier: `1`, `2`, `4`, or `8`;
- same-size multiplier: `1`, `2`, `4`, or `8`;
- stale-age multiplier: `1`, `2`, `4`, or `6`.

The file contains no source path, candidate path, candidate metadata, hash, helper policy value, raw `LargeFileMinimumBytes`, raw `SameSizeMinimumBytes`, raw `StaleAgeDays`, consent, or filesystem evidence.

The desktop stores the small preference under the current user's local-application-data `FileOp/preferences` directory. The preference is capped at 4 KiB, uses a versioned filename, and is replaced via a same-directory temporary file.

Malformed JSON, duplicate or unknown fields, an unknown schema version, unsupported multipliers, an empty/oversized file, or ordinary read failure all resolve to **no preference**, which means the current native analysis baseline. A bad preference must not block Optimize.

## Fresh-policy resolution

The persisted values are semantic multipliers, not frozen numeric thresholds.

For every new `StorageOptimizationAnalysis`, FileOp derives the display thresholds again from that analysis's current `StorageOptimizationPolicy`:

```text
large display minimum    = current native large-file baseline × remembered multiplier
same-size display minimum = current native same-size baseline × remembered multiplier
stale-age display minimum = current native stale-age baseline × remembered multiplier
```

Multiplication saturates at the corresponding numeric maximum instead of overflowing. The resulting thresholds are then passed through the existing `StorageOptimizationThresholdFilter.ValidateAgainstAnalysis(...)` same-or-stricter check before any filtering occurs.

This means a helper policy change between sessions automatically changes the numeric display thresholds. Stale raw byte/day values are never reused.

Unsupported or corrupt preferences fall back to multiplier `1,1,1`, so persistence can never request evidence below the helper's current baseline.

## UI lifetime and races

The Optimize view starts loading the preference after its threshold controls are initialized.

A generation counter prevents a late preference load from overwriting a threshold the user has already changed in the current session. Preference saves are serialized, and an older queued save is skipped if a newer threshold selection has already advanced the generation.

Every time a fresh analysis finishes loading, the current multiplier preference is resolved again against that analysis's policy before the threshold overlay is applied.

Save failure is isolated from Optimize analysis. The current in-memory threshold selection remains usable even if the small preference file cannot be written.

## Completeness and execution boundary

Persistence changes only the existing client-side display overlay. Threshold changes continue to filter the already-returned bounded analysis in memory.

This slice does not:

- rerun `AnalyzeStorageOptimizationAsync`;
- request a new indexing-helper operation;
- scan or crawl the filesystem;
- hash or open candidate content;
- add a timer or background polling loop;
- persist analysis/candidate evidence;
- change protocol v8;
- authorize cleanup or filesystem mutation.

The existing same-or-stricter completeness rule remains authoritative: FileOp must never present a persisted display threshold as evidence that the helper examined files below its native analysis baseline.

## Still separate: helper-policy transport

This persistence slice does not implement helper-policy transport for thresholds below the native analysis baseline.

Allowing the user to request smaller/younger evidence than the helper currently returns would require a separately reviewed typed policy transport and native analysis change. That work remains open because it changes what evidence the helper collects rather than merely remembering how the existing bounded result is displayed.

## Validation without GitHub Actions

`tools/verify_storage_threshold_preferences.py` models supported multiplier persistence, fresh-policy re-resolution, saturation, invalid-preference baseline fallback, strict v1 JSON parsing, and candidate-filter equivalence. Its source guards require the local-app-data/versioned-file boundary, late-load and stale-save generation checks, serialized saves, fresh-analysis policy resolution, unchanged protocol v8, and absence of helper rerun/hash/timer behavior.

The existing `tools/verify_storage_threshold_overlay.py` continues to validate the underlying same-or-stricter display overlay and its no-helper-rerun boundary.

Focused .NET tests cover preference resolution against changing policies, unsupported fallback, saturation, supported threshold round-trip, valid save/load replacement, malformed/unknown/unsupported/oversized file fallback, unsupported-save refusal, and cancellation.

Both portable verifiers remain reachable through `tools/test-local.ps1 -OfflineOnly`, so GitHub Actions are not required for this validation layer. Native/.NET/WinUI execution remains an additional local Windows gate when that runtime is available.
