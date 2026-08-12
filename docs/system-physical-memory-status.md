# System physical-memory status evidence

FileOp can capture one bounded read-only Windows physical-memory snapshot using `GlobalMemoryStatusEx`. This is machine-level memory evidence for the Performance diagnostics work; it is not a memory-health score and it does not imply that freeing, trimming or clearing memory would improve performance.

## Windows source

The Windows provider issues exactly one `GlobalMemoryStatusEx` call using the documented `MEMORYSTATUSEX` layout.

FileOp preserves only three reviewed fields:

- `ullTotalPhys` as total physical bytes;
- `ullAvailPhys` as physical bytes currently available;
- `dwMemoryLoad` as Windows' **approximate percentage of physical memory in use**.

The native structure also contains page-file and virtual-address fields because the P/Invoke layout must match Windows. This slice deliberately does not expose those fields. Their commit/page-file/virtual-address semantics are outside this reviewed boundary.

## Contract boundaries

`SystemPhysicalMemoryStatus` requires:

- nonzero total physical bytes;
- available physical bytes not greater than total physical bytes;
- Windows memory load from 0 through 100.

`UsedPhysicalBytes` is the exact arithmetic difference `TotalPhysicalBytes - AvailablePhysicalBytes`.

The contract **does not require** `WindowsMemoryLoadPercent` to equal `UsedPhysicalBytes / TotalPhysicalBytes`. Microsoft describes `dwMemoryLoad` as approximate, so FileOp preserves Windows' reported percentage independently from the byte counters instead of fabricating a consistency failure or recomputing the Windows value.

## Availability and elapsed cost

Off Windows the provider returns `Unsupported`. A failed native call returns `Unavailable` and preserves the Win32 error in detail. Impossible native values fail closed as unavailable evidence rather than being clamped.

The provider records Stopwatch elapsed time around the single native query.

## Interpretation boundary

Available physical bytes are **not** treated as wasted or recoverable RAM. A high Windows memory-load percentage is not by itself evidence of a leak, paging bottleneck or need for a “RAM cleaner”. Modern operating systems intentionally use memory for file cache and other reclaimable purposes; this slice does not attempt to assign cause from one physical-memory snapshot.

This evidence therefore does not:

- create green/yellow/red memory status;
- impose a universal free-memory or load threshold;
- recommend clearing caches or trimming working sets;
- call `EmptyWorkingSet`, `SetProcessWorkingSetSize` or another memory-reclamation API;
- stop, suspend or reprioritize processes;
- add a timer, watcher or periodic sampler;
- persist a memory history;
- add an indexing-helper operation or protocol change.

Protocol remains v8.

## Relationship to process activity

#104/#108 current machine-process activity preserves per-process CPU-time deltas and end-of-sample working/private memory. This new system snapshot describes machine-wide physical-memory availability at one instant.

FileOp does not subtract visible process working sets from total physical memory, does not treat unaccounted bytes as a leak, and does not use the system memory snapshot to re-rank process rows. A later UI can present both evidence streams while preserving their distinct provenance.

## Validation

`tools/verify_system_physical_memory_status.py` provides randomized contract modeling and source guards. Focused .NET regressions cover approximation independence, impossible values, result invariants, fake native success/failure/malformed snapshots and a Windows-native conservative sanity check.

The already-gated Performance diagnostics verifier imports and runs the memory verifier transitively, so `tools/test-local.ps1 -OfflineOnly` covers this contract without another shared PowerShell edit.

Native Windows/.NET execution remains required before release claims about actual `GlobalMemoryStatusEx` availability, field values or query cost.
