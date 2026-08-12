# Physical-disk failure prediction evidence

FileOp can query one caller-selected Windows physical disk for the storage stack's standardized failure-prediction status. This is a read-only compatibility evidence layer for #62. It is not a health score and does not parse vendor SMART attributes.

## Windows query boundary

The provider reuses `WindowsPhysicalDiskStorageApi.Open(...)`, the same reviewed physical-disk open authority used by device-context evidence. That authority opens `\\.\PhysicalDriveN` with **zero desired access** and read/write sharing.

On that already-opened handle FileOp issues exactly one `IOCTL_STORAGE_PREDICT_FAILURE` request:

- control code `0x002D1100` (`FILE_DEVICE_MASS_STORAGE`, function `0x0440`, `METHOD_BUFFERED`, `FILE_ANY_ACCESS`);
- no input buffer;
- one fixed 516-byte output buffer matching `STORAGE_PREDICT_FAILURE` (`ULONG PredictFailure` + `UCHAR VendorSpecific[512]`).

The query adapter does not open disks itself and does not issue `IOCTL_STORAGE_QUERY_PROPERTY`, ATA pass-through, NVMe protocol commands, SMART self-tests or vendor commands.

## Evidence preserved

FileOp preserves only:

- the exact physical-disk number supplied by the caller;
- Windows' raw unsigned `PredictFailure` value;
- the documented boolean interpretation `PredictFailure != 0`;
- provider availability state;
- Stopwatch-measured elapsed time and provider detail.

A nonzero value means the Windows storage stack currently reports a failure prediction through this interface. It is not converted into a FileOp severity or remaining-life estimate.

A zero value means this interface does **not currently report a predicted failure**. FileOp does not label that state `Healthy`, `Good`, `Passed` or equivalent. A zero value is not proof that the medium has no defects, that all vendor SMART attributes are normal, or that the device will not fail.

## Vendor payload boundary

`STORAGE_PREDICT_FAILURE` also returns 512 vendor-specific bytes when the device supports failure prediction. FileOp deliberately discards those bytes. Their format is vendor-specific and there is no reviewed cross-vendor parser in this slice.

Changing any or all of the 512 vendor bytes while leaving the first `PredictFailure` `ULONG` unchanged must not alter FileOp's evidence.

## Unsupported and unavailable states

Windows documents an unsupported failure-prediction device as `STATUS_INVALID_DEVICE_REQUEST`. When this reaches the user-mode `DeviceIoControl` boundary as Win32 `ERROR_INVALID_FUNCTION` (`1`), FileOp reports **Unsupported**.

Other open/query failures remain **Unavailable** with their Win32 error preserved. FileOp does not guess that access-denied, transient device, bridge, malformed-output or future errors mean either support or lack of support.

A successful native call must return the complete 516-byte structure. A shorter successful payload fails closed as unavailable evidence; the 512 vendor bytes are still never interpreted.

## Relationship to other device evidence

This evidence is separate from:

- #105 device descriptor, bus, seek-penalty and TRIM properties;
- #106 standardized NVMe SMART/Health log evidence;
- #107 post-DiskIo device-evidence presentation;
- #109/#110 volume fragmentation compatibility evidence.

In particular, FileOp does not infer SSD/HDD identity from this result and does not combine a prediction bit, NVMe warning byte, lifetime counters, TRIM support, bus type or fragmentation into a synthetic device-health score.

## Mutation and monitoring boundary

This slice adds no:

- SMART self-test;
- ATA/NVMe pass-through command;
- vendor SMART parser;
- sanitize, format, TRIM, ReTrim, defrag or optimize action;
- process/service/device control;
- registry or firmware mutation;
- timer, watcher or background health monitor;
- persistence or history;
- indexing-helper operation or protocol change.

The query is explicit and bounded to one physical disk. Protocol remains v8.

## Validation boundary

Portable model/source verification checks raw nonzero preservation, zero semantics, fixed structure size, vendor-byte independence, unsupported-vs-unavailable classification, exact reuse of the existing zero-access open authority, one prediction IOCTL, elapsed evidence, and absence of vendor parsing/mutation/monitoring paths.

Focused .NET tests additionally cover malformed payloads, fake open/query call counts, Win32 error classification and a conservative Windows-native `PhysicalDrive0` smoke result. Native Windows execution remains part of the normal local release-validation gate.
