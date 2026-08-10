# FileOp self-resource footprint

## Purpose

The Performance area now includes a small **FileOp resource footprint** card alongside the existing explicit diagnostics evidence.

This slice measures FileOp's own desktop process. It is deliberately not a machine-wide CPU/memory monitor and it does not inspect unrelated processes.

The goal is to make FileOp's own resource cost visible before adding any optimisation advice that might otherwise blame Windows, storage hardware or another workload without evidence.

## Capture boundary

The resource snapshot is taken only when FileOp already performs an explicit Performance diagnostics refresh.

There is:

- no periodic timer;
- no background sampler;
- no startup monitor loop;
- no process enumeration;
- no persistence or telemetry;
- no forced garbage collection;
- no working-set trim.

A resource-counter failure is isolated from the other Performance probes. Search, Storage, capacity and helper-owned index evidence can remain valid when FileOp's own process counters are unavailable.

## Evidence

`FileOpProcessResourceSnapshot` records:

- process start time;
- process uptime at the diagnostics capture timestamp;
- cumulative process CPU time since process start;
- current working-set bytes;
- lifetime peak working-set bytes reported by the OS;
- current private-memory bytes;
- managed-memory bytes returned by `GC.GetTotalMemory(false)`;
- current FileOp process thread count.

All size/count/duration evidence is required to be non-negative before the UI presents it.

## Memory semantics

### Working set

Working set is the current resident process working-set counter reported for FileOp. It is not equivalent to private committed memory, managed heap size, system RAM pressure or memory that Windows should immediately reclaim.

### Private memory

Private memory is the current private-byte counter for FileOp. It is presented separately from working set because the two counters describe different process-memory concepts.

### Lifetime peak working set

The peak working-set value is the largest working set Windows has reported for this FileOp process lifetime. It is historical evidence for the current process instance, not a prediction and not a claim that the peak occurred during startup.

### Managed memory

Managed memory uses `GC.GetTotalMemory(false)`. FileOp does **not** force a garbage collection to make this number smaller or more visually appealing.

This value is an estimate of managed memory currently occupied by managed objects. It is not cumulative allocated bytes, native/private memory, or proof of a leak.

## CPU semantics

`TotalProcessorTime` is cumulative CPU time used by FileOp threads since the process started.

The UI intentionally does not divide that number into a made-up "current CPU percentage" from a single snapshot. A cumulative lifetime value can include startup, idle gaps and foreground work, so it is shown only in its native time unit.

## Thread semantics

Thread count is FileOp's current process thread count. It is not a machine-wide thread count and does not identify whether any one thread is busy.

## Failure isolation

Current-process counters are read through `Process.GetCurrentProcess()` after `Refresh()`.

Expected process-counter access failures become optional status text instead of failing the whole Performance diagnostics request. FileOp does not fall back to enumerating other processes or an unrelated performance-counter provider.

## Deliberate non-goals

This slice does not:

- enumerate or rank other processes;
- infer machine-wide CPU pressure;
- infer machine-wide memory pressure;
- label a working-set value good/bad;
- diagnose a memory leak from one snapshot;
- call `GC.Collect()`;
- trim FileOp's working set;
- trim another process's working set;
- disable services/startup items;
- produce a health grade or optimisation score.

## Validation without GitHub Actions

`tools/verify_fileop_resource_footprint.py` is wired into `tools/test-local.ps1 -OfflineOnly`.

Its randomized model checks conservative non-negative normalization across process-time, memory and thread evidence. Source guards require current-process-only collection, `GC.GetTotalMemory(false)`, the UI/test wiring and the absence of process enumeration, forced GC, working-set trimming, performance-counter polling and health scoring.

Focused Core regression coverage keeps older `PerformanceDiagnosticsSnapshot` construction compatible through optional resource fields and verifies that invalid negative resource evidence is not presentation-ready.

Native WinUI/.NET execution remains part of the Windows local gate and is not claimed from the current sandbox.
