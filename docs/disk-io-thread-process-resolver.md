# Lifetime-aware DiskIo issuing-thread resolution

## Purpose

#78 decodes classic DiskIo completions into physical-disk and issuing-thread evidence. This resolver maps the issuing TID to #69's stable process identity without treating a reusable numeric TID/PID as durable ownership by itself.

The resolver belongs to one short DiskIo capture. It opens only thread IDs observed in decoded completions; it does not enumerate processes or threads.

## Stable process identity

#69 identifies a process instance by:

```text
PID + process creation time + optional image name
```

A PID without a process creation time is not returned as an owner. Image-name lookup is optional enrichment; PID + process start time remain sufficient stable identity when the path/name cannot be queried.

## Numeric IDs are reusable

Windows thread and process identifiers may be reused after the previous object terminates. Retaining an old handle does **not** make the numeric ID permanently unique.

FileOp therefore retains limited-query handles as **reuse detectors**. When a cached numeric ID appears again, FileOp performs a zero-time `WaitForSingleObject` on the cached object:

- `WAIT_TIMEOUT` — the cached object is still active, so its cached lifetime metadata may be reused;
- `WAIT_OBJECT_0` — the cached object terminated; FileOp closes and removes that entry before reopening the numeric ID;
- any other result — object state is indeterminate and the event remains unattributed.

The zero-time wait is synchronous and only occurs when a cached ID is reused by the incoming evidence. There is no polling loop or background timer.

## Access rights

Thread handles use:

```text
THREAD_QUERY_LIMITED_INFORMATION | SYNCHRONIZE
```

Process handles use:

```text
PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE
```

`SYNCHRONIZE` exists only to perform the zero-time object-state check. FileOp does not request all-access rights, tokens, process memory, suspension, termination, or other control rights.

## Event-time validation

#75 does not request `PROCESS_TRACE_MODE_RAW_TIMESTAMP`, so `ProcessTrace` converts ETW event timestamps to system FILETIME values. `GetThreadTimes` and `GetProcessTimes` return creation FILETIMEs on the same epoch.

After resolving the current Windows objects, FileOp rejects attribution if:

- the current thread was created after the I/O event; or
- the current process was created after the I/O event.

This protects delayed queued ETW records after ID reuse. If an old TID terminated and Windows already assigned that number to a newer thread, the old cached handle becomes signaled, FileOp reopens the current TID, and the newer creation time prevents the old event from being assigned to the new owner.

## Bounded caches

Defaults remain:

- **2,048** cached thread objects;
- **1,024** cached process objects.

Active entries are not evicted just to make room. Positively terminated entries may be evicted because their handles prove they no longer describe a running object for that numeric ID.

The cache belongs to a single short capture and is disposed when capture ends.

## Close-before-remove rule

When a terminated cached object is evicted, FileOp closes its handle **before** removing the dictionary entry. If `CloseHandle` fails, the old cache entry remains present so cleanup can be retried; FileOp does not lose its last managed reference to the handle.

Resolver disposal attempts every remaining cached handle even if one close reports an error, then surfaces the first cleanup failure.

## Explicit unresolved states

`WindowsDiskIoOwnerResolutionStatus` distinguishes:

- invalid/zero thread ID;
- thread cache limit reached;
- thread unavailable;
- thread creation time unavailable;
- cached thread state unavailable;
- process cache limit reached;
- process unavailable;
- native PID outside Core's signed process-ID range;
- process creation time unavailable;
- cached process state unavailable;
- thread created after the event;
- process created after the event;
- resolved.

A later provider can expose these reason counts as attribution-coverage evidence instead of hiding unresolved I/O inside an arbitrary process bucket.

## Lookup sequence

For a previously unseen/current issuing TID FileOp:

1. checks a cached TID handle if present;
2. if terminated, closes/removes it; if indeterminate, leaves the event unattributed;
3. opens the current TID with limited-query + synchronize rights;
4. reads thread creation time and current PID;
5. rejects PID `0` or a native PID outside Core's signed process-ID range;
6. checks a cached PID handle if present;
7. if terminated, closes/removes it; if indeterminate, leaves the event unattributed;
8. opens the current PID when necessary and reads process creation time;
9. validates process/thread creation times against the ETW event FILETIME;
10. returns #69 `DiskIoProcessIdentity` only for a lifetime-consistent process instance.

## Deliberate non-goals

This resolver does not:

- use PID-only `Process.GetProcessById` attribution;
- enumerate processes or threads with WMI or Toolhelp;
- poll process/thread state;
- request elevation or modify privileges;
- request all-access handles;
- read process memory;
- suspend, terminate or otherwise control a process/thread;
- create or aggregate DiskIo observations;
- add protocol/UI behavior;
- calculate a performance-health score.

## Validation without GitHub Actions

`tools/verify_disk_io_thread_process_resolver.py` models active, terminated/reused and indeterminate cached objects, creation-time validation, cache bounds and retryable close ordering. Repository guards require `WaitForSingleObject(..., 0)`, `SYNCHRONIZE`, close-before-remove, and the no-enumeration/no-escalation boundary.

Focused .NET tests use distinct fake handles for different Windows objects that reuse the same numeric ID. They cover TID/PID reuse, delayed old ETW records, indeterminate state, close-failure retry, normal lifetime validation, cache limits and cleanup.

## Next boundary

Once this resolver matches its regression suite, the Windows capture provider can safely compose #70, #76–#82 and #69 without building process attribution on a stale numeric-ID cache.
