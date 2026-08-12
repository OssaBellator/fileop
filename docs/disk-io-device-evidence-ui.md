# Post-capture physical-disk evidence in Disk I/O diagnostics

FileOp's explicit Disk I/O capture can present the read-only physical-device evidence from `physical-disk-device-context.md`, `nvme-health-evidence.md` and `physical-disk-failure-prediction.md` for physical disks that were already observed by the completed ETW capture.

This is a **post-capture enrichment** step. It does not extend the ETW session, add another capture button, or change any Disk I/O ranking, timing, attribution or process-owner selection.

## Capture and query order

The existing `DesktopSearchEngine.CaptureDiskIoAttributionAsync()` bridge is unchanged. The UI receives and renders the completed `DiskIoCaptureResult` first.

Only after a completed result contains a report does `StorageOptimizationView.ApplyDiskIoCapture` collect the report's physical-disk numbers and run `DiskIoDeviceEvidenceCollector.Query` using the existing device-context and standardized NVMe providers.

The resulting snapshot already contains only the queried rows allowed by the signed disk-number boundary and the 32-disk budget. `PerformanceDiagnosticsView.ApplyDiskIoDeviceEvidence` then calls `DiskIoDeviceEvidenceCollector.AttachFailurePrediction` with one `WindowsPhysicalDiskFailurePredictionProvider`. The attachment step queries failure prediction **only** for rows whose original device query was attempted; `DiskNumberOutOfRange` and `QueryBudgetExceeded` rows remain unchanged and do not cause a failure-prediction query.

The device queries are synchronous and bounded inside the same explicit user action. They do not create a detached task, timer, watcher or resident monitor.

`DiskIoDeviceEvidenceCollector.Query` measures the original device/NVMe query time. `AttachFailurePrediction` separately measures its bounded provider calls and returns a new snapshot whose `QueryElapsed` includes both phases. The panel therefore continues to show one post-capture device-query elapsed value that includes failure prediction but remains separate from ETW capture/response-time evidence.

If the ETW result is unsupported/unavailable, the device query is not attempted. If either post-capture enrichment phase throws, the already-rendered ETW result remains intact and only the supplementary device-evidence section reports the enrichment failure through the existing #107 catch boundary.

## Query budget and identity

`DiskIoDeviceEvidenceCollector` preserves the physical-disk order from the ETW report and rejects duplicate disk numbers before any device query starts.

At most **32 queryable physical disks** are queried per completed capture. Additional observed disks remain visible with `QueryBudgetExceeded`; they are not silently discarded. Failure prediction inherits this exact visible/query partition and does not create another 32-query allowance.

ETW physical-disk numbers are unsigned. `PhysicalDriveN` providers accept a signed `int`, so values above `int.MaxValue` are retained as explicit `DiskNumberOutOfRange` rows and no device or failure-prediction query is attempted for them.

For every originally queried row, device context and NVMe results must carry the same physical-disk number. When #115 evidence is attached, its `PhysicalDiskNumber` must also match that exact row. A mismatch fails closed instead of being displayed under the wrong ETW disk.

An already-enriched snapshot cannot be enriched a second time. This prevents an accidental duplicate native failure-prediction query from being hidden behind an idempotent-looking UI call.

The historical four-argument `DiskIoPhysicalDiskDeviceEvidence` constructor remains valid for compatibility and produces a queried row with no failure-prediction attachment. Production post-capture presentation uses the enriched snapshot; a compatibility row without #115 evidence is explicitly labeled as not attached rather than inferred from device/NVMe fields.

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

For Windows storage-stack failure prediction, each queried row gets one separate annotation:

- raw zero: **no current prediction reported (raw 0)**;
- raw nonzero: **failure prediction reported (raw N)** with the exact unsigned value retained;
- unsupported/unavailable: the provider state/detail remains explicit;
- provider elapsed time is shown on the annotation.

A raw zero is not displayed as `Healthy`, `Good`, `Passed` or as proof the medium has no defects. A nonzero result is reported Windows compatibility evidence, not a FileOp severity or remaining-life score. The 512 vendor-specific bytes from `STORAGE_PREDICT_FAILURE` remain discarded by #115 and never enter this view.

The panel explicitly says the fields are not an SSD/HDD classification, health score, bottleneck verdict or recommendation. Unsupported and unavailable evidence stays explicit.

## Independence from Storage readiness

System-wide Disk I/O capture remains independent of native Storage optimization/index readiness. `StorageOptimizationView.SetUnavailable` and `SetReadyForRefresh` must not acquire DiskIo capture/device-query behavior merely because the native reclaim surface becomes unavailable or busy.

Device evidence is reset only when a new Disk I/O capture begins or the Disk I/O capture itself is reset. An unavailable Disk I/O capture marks device evidence as not queried; unrelated Storage source availability does not retroactively change a valid Disk I/O result.

## Scope boundary

This slice does not:

- change `CaptureDiskIoAttributionAsync` or the ETW provider/session lifecycle;
- query disks not already present in the completed ETW report;
- query more than the existing 32 signed physical-disk rows per capture;
- add a second device/health capture action;
- sort/promote disks by device, NVMe or failure-prediction evidence;
- create a health/reliability/wear/performance/failure score;
- infer SSD/HDD from bus, seek penalty or TRIM;
- apply temperature, spare, life-used, error-count or failure-prediction severity thresholds;
- parse the 512 vendor-specific failure-prediction bytes or ATA/SATA/vendor SMART attributes;
- issue SMART self-tests, ATA/SCSI/NVMe pass-through, TRIM, defrag, optimize or any storage mutation;
- persist device health history;
- add a background sampler or protocol operation.

Protocol remains v8. Vendor-specific SMART parsing remains outside this presentation.

## Validation

`tools/verify_disk_io_device_evidence_ui.py` models the original 32-disk query partition plus the additive failure-prediction attachment. It requires attachment to preserve row order, query only the already-queried rows, preserve skipped rows unchanged, reject duplicate attachment and remain independent from raw zero/nonzero values for selection/order.

Focused .NET regressions verify the same attachment boundaries, exact physical-disk identity, compatibility of the existing four-argument row shape and non-negative combined elapsed evidence. Source/UI guards require neutral zero/nonzero presentation, raw-value preservation, the existing supplementary-failure isolation, no DiskIo ranking change and the already-gated Performance DiskIo verifier chain.

Native Windows/.NET/WinUI execution is still required before release claims about live device queries and panel rendering.
