# Post-capture physical-disk evidence in Disk I/O diagnostics

FileOp's explicit Disk I/O capture can now present the read-only physical-device evidence from `physical-disk-device-context.md` and `nvme-health-evidence.md` for physical disks that were already observed by the completed ETW capture.

This is a **post-capture enrichment** step. It does not extend the ETW session, add another capture button, or change any Disk I/O ranking, timing, attribution or process-owner selection.

## Capture and query order

The existing `DesktopSearchEngine.CaptureDiskIoAttributionAsync()` bridge is unchanged. The UI receives and renders the completed `DiskIoCaptureResult` first.

Only after a completed result contains a report does `StorageOptimizationView.ApplyDiskIoCapture` collect the report's physical-disk numbers and run `DiskIoDeviceEvidenceCollector.Query` using:

- `WindowsPhysicalDiskDeviceContextProvider`;
- `WindowsNvmeHealthEvidenceProvider`.

The device queries are synchronous and bounded inside the same explicit user action. They do not create a detached task, timer, watcher or resident monitor.

If the ETW result is unsupported/unavailable, the device query is not attempted. If post-capture enrichment throws, the already-rendered ETW result remains intact and only the device-evidence section reports the enrichment failure.

## Query budget and identity

`DiskIoDeviceEvidenceCollector` preserves the physical-disk order from the ETW report and rejects duplicate disk numbers before any device query starts.

At most **32 queryable physical disks** are queried per completed capture. Additional observed disks remain visible with `QueryBudgetExceeded`; they are not silently discarded.

ETW physical-disk numbers are unsigned. `PhysicalDriveN` providers accept a signed `int`, so values above `int.MaxValue` are retained as explicit `DiskNumberOutOfRange` rows and no device query is attempted for them.

For every queried row, both returned provider results must carry the same physical-disk number. A mismatched result fails closed instead of being attached to the wrong ETW disk.

## Presentation boundary

The device-evidence panel is appended below the existing Disk I/O tables. It does not participate in:

- observed-byte ordering;
- operation counts;
- response-time p50/p95 calculations;
- process-owner attribution or process timing;
- the threshold-free bottleneck-evidence selectors.

For device context, FileOp displays the reported model/bus/revision/serial/removable/queueing fields and the three-state seek-penalty/TRIM evidence.

For standardized NVMe SMART/Health, FileOp displays the raw warning byte/defined warning names, Kelvin temperature, spare/threshold, raw life-used estimate, full lifetime counters and lifetime temperature-time counters. `PercentageUsed=255` is displayed as a life-used estimate greater than 254 with the raw value retained.

`no currently defined warning bits set` is deliberately descriptive. It is not displayed as a healthy result.

The panel explicitly says the fields are not an SSD/HDD classification, health score, bottleneck verdict or recommendation. Unsupported and unavailable evidence stays explicit.

## Independence from Storage readiness

System-wide Disk I/O capture remains independent of native Storage optimization/index readiness. `StorageOptimizationView.SetUnavailable` and `SetReadyForRefresh` must not acquire DiskIo capture/device-query behavior merely because the native reclaim surface becomes unavailable or busy.

Device evidence is reset only when a new Disk I/O capture begins or the Disk I/O capture itself is reset. An unavailable Disk I/O capture marks device evidence as not queried; unrelated Storage source availability does not retroactively change a valid Disk I/O result.

## Scope boundary

This slice does not:

- change `CaptureDiskIoAttributionAsync` or the ETW provider/session lifecycle;
- query disks not already present in the completed ETW report;
- query more than 32 signed physical-disk numbers per capture;
- add a second device/health capture action;
- sort/promote disks by device or NVMe health fields;
- create a health/reliability/wear/performance score;
- infer SSD/HDD from bus, seek penalty or TRIM;
- apply temperature, spare, life-used or error-count severity thresholds;
- issue SMART self-tests, NVMe pass-through, TRIM, defrag, optimize or any storage mutation;
- persist device health history;
- add a background sampler or protocol operation.

Protocol remains v8. ATA/SATA/vendor SMART remains outside this standardized NVMe presentation.

## Validation

`tools/verify_disk_io_device_evidence_ui.py` contains a randomized collector/presentation-state model and repository source guards. Focused .NET regressions verify order, provider call count, duplicate rejection, signed-range handling, the 32-disk budget and provider-result disk identity.

The already-gated Performance DiskIo UI verifier invokes the new verifier transitively; the child verifier pins the parent model/source calls and the existing `tools/test-local.ps1 -OfflineOnly` entry.

Native Windows/.NET/WinUI execution is still required before release claims about live device queries and panel rendering.
