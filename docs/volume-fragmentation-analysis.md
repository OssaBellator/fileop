# Read-only volume fragmentation analysis evidence

FileOp can request one explicit fragmentation analysis for a **local drive-letter root** such as `C:\`. This is compatibility evidence for the open TRIM/defrag diagnostics work; it is not an optimize action and it does not mean FileOp recommends defragmentation.

## Windows evidence source

The Windows provider uses `System.Management` 10.0.10 to access the local `Root\CIMV2` WMI namespace and locate the exact `Win32_Volume` whose `Name` matches the requested canonical drive root.

The provider invokes only the legacy `Win32_Volume.DefragAnalysis` method. Microsoft documents `DefragAnalysis` as generating a fragmentation analysis and returning:

- a raw provider return code;
- `DefragRecommended`, which is **Windows' legacy provider recommendation**, not FileOp's recommendation;
- an embedded `Win32_DefragAnalysis` object containing structured fragmentation metrics when the call succeeds.

Microsoft now publishes this VDS WMI surface under previous-version documentation and lists no supported client version. FileOp therefore treats it as **optional compatibility evidence**. Unsupported/missing WMI classes or methods remain explicit and cannot be converted into a healthy/optimized result.

The newer `MSFT_Volume.Optimize(Analyze=true)` / `Optimize-Volume -Analyze` path is the current Windows storage analysis surface, but its documented method output does not expose the same structured fragmentation metrics. This slice does not parse PowerShell verbose text or claim that a successful analyze-only return code is equivalent to a structured fragmentation report.

## Preserved structured fields

On provider return code `0`, FileOp requires a complete embedded analysis payload and preserves:

- Windows' `DefragRecommended` Boolean as `WindowsDefragRecommended`;
- file fragmentation percentage;
- average fragments per file;
- total file count;
- total fragmented-file count;
- total free-space extent count;
- largest free-space extent bytes;
- average free-space extent bytes;
- volume size bytes;
- used-space bytes;
- free-space bytes.

The contract rejects percentages above 100, non-finite/negative averages, fragmented-file counts above total files, individual used/free values above volume size, or a largest free-space extent larger than total free space. A WMI success code with missing or malformed structured fields fails closed as unavailable evidence.

`WindowsDefragRecommended=true` remains a reported provider field. FileOp does not turn it into an automatic action, urgency/severity, or a statement that defrag is beneficial for the current storage stack.

## Input and lookup bounds

This first slice supports only explicit local drive-letter roots. Inputs such as `C:\data`, UNC paths and volume-GUID roots are rejected rather than silently mapped to a different volume.

The WMI lookup enumerates at most **128** `Win32_Volume` instances and matches `Name` case-insensitively against the exact canonical root. This avoids WQL string interpolation and provides a hard lookup bound.

Directory mount-point volumes are deliberately outside this slice because FileOp's current volume-capacity authority is drive-root based. A later mount-point-aware design must resolve the actual volume identity instead of collapsing a mount point to its containing drive.

## Timeout and cancellation

`VolumeFragmentationAnalysisBudget` defaults to a **60-second** analysis timeout and allows explicit values from 1 second through 5 minutes.

The provider:

- propagates caller cancellation;
- links an internal timeout cancellation token to the WMI analysis operation;
- uses `ManagementOperationObserver` for asynchronous method invocation;
- calls `ManagementOperationObserver.Cancel()` when the linked token is cancelled;
- records Stopwatch elapsed time separately from the returned fragmentation metrics.

Microsoft documents WMI `CallCanceled` as a cancellation state that, depending on timing/provider behavior, may not mean the underlying operation truly stopped. FileOp therefore describes timeout as **cancellation requested / FileOp stopped waiting**, not proof that the Windows provider immediately ceased work. Native Windows validation remains required for actual cancellation behavior.

## Raw provider states

The legacy method's documented raw return code remains attached to non-success results. FileOp maps only broad availability state:

- `0` — completed only when complete structured evidence is also present;
- `1` — permission required;
- `2` — unsupported;
- `6` — cancelled;
- all other documented or future nonzero codes — unavailable, with the raw code preserved.

This avoids inventing health or optimization semantics from conditions such as dirty volume, insufficient free space, defrag-engine busy/error, or future provider codes.

## Mutation boundary

This slice does **not**:

- invoke `Win32_Volume.Defrag`;
- invoke `MSFT_Volume.Optimize`;
- run `Optimize-Volume`, `defrag.exe`, PowerShell or another child process;
- request `ReTrim`, `Defrag`, slab consolidation or tier optimization;
- issue TRIM/defrag/optimize filesystem controls;
- schedule or automatically repeat analysis;
- infer SSD/HDD media type;
- create an optimization, health, wear or urgency score;
- add a UI action or storage mutation;
- add an indexing-helper protocol operation.

Protocol remains v8.

## Relationship to TRIM evidence

#105 already preserves the physical device's three-state `StorageDeviceTrimProperty` evidence separately. Device TRIM capability/status is not proof that re-trimming this volume now would improve performance. This fragmentation slice likewise does not use bus type, seek-penalty or TRIM support to decide whether defragmentation is beneficial.

A later UI can show the two evidence streams side by side only if it preserves these independence boundaries.

## Validation

`tools/verify_volume_fragmentation_analysis.py` provides randomized contract/provider-state modeling plus repository source guards. Focused .NET regressions cover drive-root identity, impossible metrics, raw return-code mapping, malformed-success fail-closed behavior, exact provider call arguments, caller cancellation and result invariants.

`tools/test-local.ps1 -OfflineOnly` runs the verifier directly. Native Windows/.NET execution remains required before release claims about live WMI availability, structured output, permission requirements, elapsed cost or cancellation behavior on supported Windows versions.
