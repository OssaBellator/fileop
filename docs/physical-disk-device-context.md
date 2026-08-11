# Physical-disk device context evidence

FileOp can query read-only Windows storage properties for a **known physical-disk number**. This foundation is descriptive device context for later Performance diagnostics; it is not a storage-health verdict and does not change the disk.

## Query boundary

`WindowsPhysicalDiskDeviceContextProvider` opens only the requested `\\.\PhysicalDriveN` with **zero desired access** and read/write sharing, then issues bounded `IOCTL_STORAGE_QUERY_PROPERTY` requests.

The provider does not enumerate files, volumes, partitions, registry startup state or filesystem content. It does not send a write-capable storage command.

The required `StorageDeviceProperty` descriptor contributes:

- physical-disk number;
- raw `STORAGE_BUS_TYPE` numeric value and a descriptive label;
- optional vendor ID;
- optional product ID;
- optional product revision;
- optional serial number;
- removable-media flag;
- command-queueing flag.

The descriptor strings are documented by Windows as null-terminated ASCII offsets inside the returned descriptor. FileOp validates every nonzero offset, terminator and printable ASCII byte before presenting it.

## Bus-type evidence

The raw bus value remains an unsigned numeric value. FileOp maps currently documented values to labels such as SATA, USB, SAS and NVMe, but the numeric evidence is preserved independently.

`BusTypeMaxReserved` is currently `0x7F`. Values above that documented reserved maximum are rejected as malformed descriptor data. Values inside the reserved numeric space that FileOp does not know remain explicit as `Unrecognized bus type N` rather than being narrowed, silently remapped or treated as a known media type.

Bus transport is **not** a disk-health or media-type classifier. In particular, FileOp does not infer SSD/HDD status from NVMe, SATA, USB or any other bus label.

## Seek-penalty and TRIM evidence

FileOp separately queries:

- `StorageDeviceSeekPenaltyProperty` / `DEVICE_SEEK_PENALTY_DESCRIPTOR`;
- `StorageDeviceTrimProperty` / `DEVICE_TRIM_DESCRIPTOR`.

Each capability has one of three evidence states:

- `Available`: the driver returned a valid descriptor and FileOp preserves the reported boolean;
- `Unsupported`: the device/driver reports that the property is not supported;
- `Unavailable`: the query or returned descriptor could not be used reliably.

Those states are intentionally distinct. `Unsupported` and `Unavailable` are never converted to `false`.

A reported seek-penalty boolean is presented only as that Windows property. FileOp does not convert it into a rotational-media assertion. A reported TRIM boolean is likewise capability/status evidence, not proof of device health, wear state or whether running a manual trim operation would improve performance.

## Malformed evidence

The parser fails closed when required device-descriptor evidence is structurally invalid, including:

- a returned buffer smaller than the fixed descriptor fields;
- `Version` or `Size` smaller than the known structure;
- `Size` extending beyond the returned bytes;
- a bus value above the documented `0x7F` reserved maximum;
- a nonzero string offset inside the fixed header or outside the descriptor;
- an unterminated or non-printable ASCII descriptor string.

Malformed optional seek-penalty or TRIM descriptors make only that capability `Unavailable`; they do not discard an otherwise valid device descriptor.

## Scope boundary

This slice does **not**:

- infer SSD, HDD, rotational media, flash media, health grade or bottleneck state;
- read SMART/NVMe health log pages;
- query temperature, endurance, unsafe-shutdown or wear data;
- issue TRIM, defrag, optimize, format or filesystem-control commands;
- open a physical disk with read or write data access;
- change disk cache, power, queueing or firmware settings;
- add a watcher, timer, resident sampler or persistence;
- add an indexing-helper protocol operation.

Protocol remains v8. SMART/NVMe health and TRIM/defrag applicability remain separately reviewable evidence layers because their support, privilege and interpretation boundaries differ from these device properties.

## Validation

`tools/verify_physical_disk_device_context.py` contains a portable randomized descriptor model plus source guards. Focused .NET tests exercise descriptor offsets/ASCII parsing, known and unknown bus values, rejection above `0x7F`, boolean descriptor validation, capability-state invariants and a failure-tolerant Windows-native query.

Native Windows/.NET execution remains required before release claims about live `DeviceIoControl` behavior.
