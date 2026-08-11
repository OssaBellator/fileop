# Standardized NVMe SMART/Health evidence

FileOp can query the standardized NVMe SMART / Health Information Log for a **known physical-disk number** on Windows. This is a read-only evidence layer for NVMe devices; it is not a generic disk-health score and does not imply equivalent support for ATA/SATA, USB bridges or vendor-specific SMART formats.

## Windows query path

The Windows provider uses the same zero-desired-access physical-disk open boundary as `WindowsPhysicalDiskDeviceContextProvider`, then issues `IOCTL_STORAGE_QUERY_PROPERTY` with:

- `StorageDeviceProtocolSpecificProperty`;
- `PropertyStandardQuery`;
- `ProtocolTypeNvme`;
- `NVMeDataTypeLogPage`;
- the standardized NVMe SMART/Health log-page identifier;
- a protocol-data offset equal to the 40-byte `STORAGE_PROTOCOL_SPECIFIC_DATA` structure;
- a 512-byte protocol-data length.

Microsoft documents this protocol-specific query path for NVMe monitoring/inventory and provides the SMART/Health query as an example. FileOp does not use `IOCTL_STORAGE_PROTOCOL_COMMAND`, vendor pass-through, a Set Property request, or any behavior-changing NVMe command.

The returned `STORAGE_PROTOCOL_DATA_DESCRIPTOR` is validated before the health bytes are read. FileOp requires:

- enough returned bytes for the 48-byte descriptor header;
- descriptor `Version` and `Size` to equal the documented 48-byte descriptor size;
- returned protocol type = NVMe;
- returned data type = NVMe log page;
- returned request value = SMART/Health log page;
- protocol data offset at least 40 bytes;
- protocol data length at least 512 bytes;
- the complete 512-byte health payload to fit inside the actual returned byte count, using overflow-safe range arithmetic.

Malformed protocol metadata fails closed instead of being interpreted as a health log.

## Preserved health fields

The 512-byte standardized `NVME_HEALTH_INFO_LOG` contributes:

- the raw critical-warning byte;
- the five currently defined critical-warning flags: available spare below threshold, temperature threshold, reliability degraded, media read-only, and volatile-memory backup failure;
- any reserved/future warning bits, preserved without interpretation;
- composite temperature in **Kelvin**;
- available spare percentage;
- available spare threshold percentage;
- the NVMe `PercentageUsed` field;
- 128-bit power-cycle count;
- 128-bit power-on-hours count;
- 128-bit unsafe-shutdown count;
- 128-bit media-error count;
- 128-bit error-information-log entry count;
- warning-composite-temperature minutes;
- critical-composite-temperature minutes.

The five lifetime counters remain full unsigned 128-bit values; FileOp does not truncate them to 64 bits.

## Interpretation boundaries

The critical-warning byte describes current standardized warning state. Reserved bits are retained but never assigned a FileOp meaning.

`AvailableSpare` and `AvailableSpareThreshold` are normalized 0–100 percentages. Values outside that documented range make the health payload malformed.

`PercentageUsed` is **not a health percentage**. NVMe defines it as a vendor-specific estimate of NVM life used based on actual usage and the manufacturer's prediction. A value of 100 means estimated endurance has been consumed but does not by itself indicate device failure; values can exceed 100 and 255 represents values greater than 254. FileOp therefore preserves the raw byte and never converts it to “health remaining.”

Composite temperature is preserved in Kelvin as reported. This slice does not invent a temperature severity threshold. Warning/critical temperature-time counters are lifetime minutes, not evidence that the device is currently over-temperature.

Unsafe shutdowns, media errors and error-log entries are lifetime counters. FileOp does not convert one nonzero counter into a failure verdict; changes over time would require a separately reviewed history/persistence design.

## Support boundary

A failed protocol-specific query is classified as `Unsupported` only for Windows errors that indicate the requested property/protocol is not supported or the request shape is invalid for that device. Other failures are `Unavailable`. Neither state is converted into a healthy result.

This slice deliberately supports only the standardized NVMe query. ATA/SATA SMART data, USB bridge vendor commands and other protocol-specific formats remain unsupported until their semantics and access boundaries are separately reviewed.

## Scope boundary

This slice does **not**:

- create a disk health, reliability, wear or performance score;
- label the device healthy simply because no critical-warning bit is set;
- treat `PercentageUsed` as “health remaining”;
- infer an SSD/HDD media type;
- read vendor-specific SMART attributes or vendor log pages;
- issue NVMe pass-through or vendor commands;
- change temperature thresholds, firmware, power, cache or namespace settings;
- run self-tests, sanitize, format, TRIM, defrag or optimize commands;
- add a timer, watcher, persistence or background health monitor;
- add an indexing-helper protocol operation.

Protocol remains v8. A later UI may present these fields only as standardized NVMe evidence and must preserve the same unsupported/unavailable and no-score boundaries.

## Validation

`tools/verify_nvme_health_evidence.py` provides a portable randomized model for the query layout and 512-byte health parser plus repository source guards. Focused .NET tests cover exact query fields, critical-warning bits, 128-bit counters, normalized spare validation, exact descriptor/protocol/range validation, reserved warning bits and result invariants.

The already-gated DiskIo bottleneck verifier runs the NVMe verifier transitively, and the NVMe verifier pins that parent→child chain. Native Windows/.NET execution is still required before release claims about live NVMe `DeviceIoControl` behavior.
