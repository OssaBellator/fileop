# Current free space and native Storage history

## Purpose

FileOp can already show current volume free space and native aggregate Storage history. This slice puts those observations beside each other without turning two historical points into a forecast.

The evidence answers four narrow questions:

1. how many current free bytes remain on the active volume;
2. how large the latest native aggregate observation was;
3. how much the newest observation changed from the immediately preceding observation;
4. when that change is an exact **physical-allocation increase**, how many multiples of that one increase fit inside the current free-space observation.

The fourth value is a descriptive ratio, not a time estimate.

## Evidence boundary

`StorageHistoryPressureEvidence` uses only:

- the latest two persisted `StorageHistorySnapshot` observations for one root;
- current `DriveInfo.TotalSize` and `DriveInfo.AvailableFreeSpace` for that same root.

It does not scan directories, enumerate files, query another service operation, add a timer, or persist another history series.

Current capacity uses the same shared `DesktopSearchEngine.ReadVolumeCapacity` helper as the Performance diagnostics surface.

## Physical versus logical history

Free volume bytes are physical-capacity evidence. FileOp therefore compares them with historical growth only when **both** of the newest two snapshots have known physical allocation.

When both allocated values are known:

```text
physical change = newer allocated bytes - older allocated bytes
```

When either allocated value is unknown, FileOp still shows the signed logical-size change so the user can see that the indexed namespace changed, but it labels the free-space comparison **Not comparable**.

Logical-size growth is never substituted for physical allocation in the headroom ratio.

## Descriptive headroom ratio

For a positive exact physical change and known current free bytes:

```text
ratio = current free bytes / last observed physical-allocation increase
```

For example, a 2 GiB physical increase followed by a current 20 GiB free-space observation is displayed as `10× last growth`.

This does **not** mean ten hours, ten days, ten future observations, or ten repetitions remain. The history interval may not represent future workload, and unrelated filesystem activity may change free space between observations.

FileOp deliberately does not calculate:

- growth per hour/day;
- time to full;
- days remaining;
- a disk-full date;
- a trend line from the newest pair;
- cause attribution from aggregate correlation.

## Non-positive change

If the newest exact physical observation is flat or smaller than the previous one, the UI says **No positive growth**. It does not divide by zero, use an absolute delta, or manufacture a pressure ratio from shrinkage.

## Sparse history

With no observations, there is no historical comparison.

With one observation, FileOp can show the latest indexed size but says a second trustworthy observation is required before it describes a change.

Only the newest two observations feed the pressure comparison. The longer History timeline remains visible separately and is not compressed into a synthetic trend score.

## Capacity race handling

Capacity reads are treated conservatively:

- negative free bytes clamp to zero;
- non-positive total capacity is treated as unknown;
- if a raced free-space read exceeds the known total, the comparison clamps free bytes to total;
- free percentage is bounded to `[0, 100]`.

This prevents a transient capacity race from overstating headroom.

## UI wording

The History view presents:

- **Current free** — bytes plus current free percentage when total capacity is known;
- **Latest indexed storage** — latest physical allocation when known, otherwise logical size;
- **Last observed change** — signed physical or logical delta plus the actual observation interval;
- **Free space / last growth** — the descriptive multiple only for a positive exact physical delta.

The detail sentence explicitly states that the ratio is one-interval descriptive evidence, not a forecast, trend guarantee, cause attribution, or disk-full date.

## Validation without hosted Actions

`tools/verify_storage_pressure_history.py` performs randomized arithmetic/property checks and repository guards requiring:

- physical-only free-space/growth comparison;
- logical and mixed-allocation fallback without headroom claims;
- non-positive physical changes to produce no ratio;
- raced free-space clamping;
- the shared capacity reader;
- the History UI wording and state handling;
- no timer/background sampler or time-to-full calculation;
- focused .NET domain regressions.

The verifier is wired into `tools/test-local.ps1 -OfflineOnly`. The portable model uses 50,000 randomized cases; Windows/.NET compilation and UI execution remain part of the local Windows gate when that toolchain is available.
