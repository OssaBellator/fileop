# Lifetime-aware DiskIo issuing-thread resolution

## Purpose

#78 decodes a classic DiskIo completion into physical disk, operation, bytes, response time and the **issuing thread ID**. This slice resolves that issuing thread to a stable #69 process identity without treating a reusable PID or TID as durable identity by itself.

The resolver is scoped to one short DiskIo capture session. It opens only thread IDs observed in decoded completions; it does not enumerate processes or threads.

## Stable identity rule

#69 defines process ownership as:

```text
PID + process creation time + optional image name
```

The Windows resolver preserves that boundary. A PID without a process creation time is not returned as an owner.

Image-name lookup is optional enrichment. If `QueryFullProcessImageNameW` fails after PID + process creation time are proven, FileOp may still return a stable owner with `ImageName = null`.

## Why the resolver pins handles

Windows thread and process identifiers can be reused after the corresponding object has terminated and been released. During a FileOp capture the resolver therefore retains limited-query handles for successfully resolved thread/process objects.

Holding the object handle through the short capture gives the cache a stable object lifetime instead of repeatedly asking whether the same numeric ID still refers to the same object.

The resolver has hard cache bounds:

- default maximum cached threads: **2,048**;
- default maximum cached processes: **1,024**.

When a cap is reached, a new identity is reported as unresolved. FileOp does not evict a pinned identity and silently replace it with a newer object that reused the same numeric ID during the same capture.

The cache belongs to the capture session and is disposed when that capture ends.

## Event and creation-time comparison

#75 does not request `PROCESS_TRACE_MODE_RAW_TIMESTAMP`. On FileOp's supported Windows versions, `ProcessTrace` supplies `EVENT_HEADER.TimeStamp` as system time in FILETIME units: 100-nanosecond intervals since January 1, 1601 UTC.

`GetThreadTimes` and `GetProcessTimes` return their creation times as FILETIME values on the same epoch. FileOp converts all three through UTC FILETIME conversion before comparing lifetimes.

A lookup is rejected if:

- the thread creation time is later than the I/O event time; or
- the process creation time is later than the I/O event time.

Those states are explicit evidence of an ID/lifetime mismatch. The event remains unattributed instead of being assigned to the current owner of a reused ID.

A thread that is already opened and found to start after an older event remains pinned. A later event from that same current thread can then resolve without reopening the numeric TID.

A process found to start after an event is **not cached** for that failed attempt. The temporary process/thread handles are released so a later event can retry and establish a valid lifetime relationship.

## Lookup sequence

For a previously unseen issuing thread ID, FileOp performs:

1. `OpenThread(THREAD_QUERY_LIMITED_INFORMATION)`;
2. `GetThreadTimes` for thread creation FILETIME;
3. `GetProcessIdOfThread`;
4. reject PID `0` or a native PID outside Core's signed process-ID range;
5. reuse an already pinned process entry when available, otherwise:
   - `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`;
   - `GetProcessTimes` for process creation FILETIME;
   - verify process creation is not later than the event;
   - best-effort `QueryFullProcessImageNameW`;
   - pin the process handle;
6. pin the thread handle;
7. verify both cached creation times against the observation time;
8. return #69 `DiskIoProcessIdentity` only when the stable lifetime checks succeed.

The resolver asks only for limited query rights. It does not request all-access thread/process handles.

## Explicit unresolved states

`WindowsDiskIoOwnerResolutionStatus` distinguishes:

- invalid/zero thread ID;
- thread cache limit reached;
- thread unavailable;
- thread creation time unavailable;
- process cache limit reached;
- process unavailable;
- native PID outside Core's identity range;
- process creation time unavailable;
- thread created after the event;
- process created after the event;
- resolved.

The later capture report can aggregate these reasons as attribution coverage evidence instead of hiding unresolved I/O inside an arbitrary “System” bucket.

## Cleanup

Temporary handles are released on every failed path.

Pinned thread/process handles are closed when the resolver is disposed. Cleanup attempts **every** cached handle even if one close reports an error; after all attempts, the first cleanup failure is surfaced.

This makes resource release best-effort-complete without presenting a partial cleanup as success.

## Deliberate non-goals

This resolver does not:

- use `Process.GetProcessById` as a PID-only identity shortcut;
- use WMI or Toolhelp process/thread enumeration;
- poll processes or threads;
- request elevation;
- modify privileges, ACLs or group membership;
- request `PROCESS_ALL_ACCESS` or `THREAD_ALL_ACCESS`;
- open process tokens;
- suspend, terminate or otherwise control a process/thread;
- read process memory;
- create `DiskIoEventObservation` values;
- aggregate attribution;
- add protocol/UI behavior;
- calculate a bottleneck or health score.

## Next boundary

After this resolver is reviewed, the capture provider can combine:

- #76 native consumer transport;
- #77 immutable event snapshots;
- #78 DiskIo completion decoding;
- this lifetime-aware owner resolver;
- #69 attribution aggregation;
- #70 capture duration/event caps and cancellation.

That provider should also report attribution coverage, unresolved-reason counts and ETW event/buffer loss before the UI makes any storage-performance recommendation.

## Validation without GitHub Actions

`tools/verify_disk_io_thread_process_resolver.py` models bounded thread/process caches, lifetime comparisons, retry behavior and PID/TID reuse across randomized events. Repository guards pin limited-query native APIs and forbid process enumeration, privilege escalation, process control and PID-only attribution shortcuts.

Focused .NET tests use an injected fake lifetime API to cover stable resolution, cache reuse, both lifetime-mismatch directions, lookup failures, cache ceilings, out-of-range PIDs and cleanup semantics without opening real Windows processes or threads.
