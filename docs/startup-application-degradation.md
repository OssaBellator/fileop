# Windows startup application degradation compatibility evidence

FileOp can read a bounded set of retained Windows Diagnostics-Performance **application-startup degradation** events as optional historical compatibility evidence.

This is not a census of programs configured to start with Windows, not a reconstruction of every boot, and not a FileOp startup-impact score. It is one Windows event-log evidence stream that may be absent, disabled, inaccessible, rotated away or schema-incompatible on a given machine.

## Windows event source

The Windows provider uses `System.Diagnostics.EventLog` 10.0.10 and queries only:

- channel `Microsoft-Windows-Diagnostics-Performance/Operational`;
- provider `Microsoft-Windows-Diagnostics-Performance`;
- Event ID `101`.

The query is read newest-first. FileOp does not call `FormatDescription()` or parse localized event-message text. Each retained record is converted through `EventRecord.ToXml()` and FileOp parses named XML fields.

Microsoft documents `EventLogReader` as the event-query reader, `ReadEvent(TimeSpan)` as a bounded read operation, `CancelReading()` as cancellation of the current query, and `ToXml()` as the complete XML representation of an event. Microsoft does not provide FileOp with a current universal compatibility guarantee for the specific Event 101 payload shape used here, so the payload is treated as **optional compatibility telemetry** and fails closed when required named fields differ or disappear.

## Bounded query

`StartupApplicationDegradationBudget` defaults to:

- **2 seconds** of total read budget;
- **20 visible events**.

The allowed read budget is 100 ms through 10 seconds. The hard visible-event ceiling is 100.

Every `ReadEvent` receives only the remaining FileOp read budget. A no-record return at or after the total budget is treated as a timeout rather than proof of end-of-log. Caller cancellation calls `EventLogReader.CancelReading()` and is propagated.

The reader fetches at most `MaxEvents + 1` matching records. The extra record is used only to preserve a `MoreMatchingEventsAvailable` bit, so older matching events are not silently presented as though the visible list were complete.

Each individual event XML document has a 65,536-character compatibility ceiling before parsing.

The synchronous EventLogReader/query setup itself is still Windows platform code and is not claimed to be preemptively cancellable at every internal step. Native Windows validation remains required for actual blocking/cancellation behavior.

## Preserved Event 101 fields

For every visible matching record FileOp preserves:

- event-log record ID;
- optional event version;
- event recorded timestamp;
- Windows-reported incident/start timestamp;
- component `Name`;
- optional `FriendlyName`;
- optional component `Version`;
- raw Windows-reported `TotalTime` milliseconds;
- raw Windows-reported `DegradationTime` milliseconds.

Required provider/channel/event identity is validated from the XML `System` element. `EventData` entries must be named and unique. `Name`, `TotalTime`, `DegradationTime` and `StartTime` are required. Blank optional friendly-name/version values normalize to absent.

The parser deliberately does **not** assume `DegradationTime <= TotalTime`, derive a percentage, impose a universal millisecond threshold, or turn either duration into FileOp severity. The raw values remain Windows-reported compatibility evidence.

Visible results are newest-first and record IDs must be unique. FileOp does not re-rank events by duration or component name.

## Empty, unavailable and truncated states

A completed query with zero retained Event 101 records means only that the bounded query returned no retained matching compatibility events. It is **not proof** that startup was fast, that no startup application had impact, or that the event channel has historically captured every relevant application.

If the Diagnostics-Performance channel is missing, the result is `Unsupported`. Access failure is `PermissionRequired`. Exhausting the bounded read budget is `Cancelled`. Malformed XML/schema, other event-log errors or unexpected provider failures are `Unavailable`.

No unavailable state is converted into a healthy/fast result.

## Relationship to current process activity

#104/#108 measure **current** machine process activity across an explicit two-frame sample using stable PID + process-start identity. Event 101 is historical event-log evidence and does not provide the same current process identity.

FileOp therefore does not automatically join a historical Event 101 component name to a currently running PID, and it does not infer that a currently busy process caused historical startup degradation.

A later UI may present the two evidence streams side by side only if it preserves this distinction.

## Registry/startup-registration boundary

This slice does not inspect or mutate:

- Run/RunOnce registry keys;
- Startup folders;
- packaged StartupTask registration;
- services or scheduled tasks;
- process priority, affinity or lifetime.

Registration presence alone is not measured startup impact. FileOp does not recommend disabling an application merely because a startup registration exists or because one compatibility event was retained.

## Scope boundary

This slice adds no:

- background EventLog watcher or subscription;
- periodic sampler/timer;
- persisted startup history;
- startup/health/impact score;
- process stop/suspend/kill/reprioritization action;
- service/task/registry control;
- indexing-helper operation or protocol change.

Protocol remains v8.

## Validation

`tools/verify_startup_application_degradation.py` provides randomized newest-first/truncation/raw-duration modeling plus repository source guards. Focused .NET regressions cover named XML parsing, provider/event/channel mismatch, duplicate/missing/malformed data, blank optional fields, XML size bound, newest-first/result invariants, shared read timeout, caller cancellation, empty-result wording and visible truncation evidence.

The already-gated machine-process activity verifier imports and runs the startup-degradation model/source checker transitively. Native Windows/.NET/EventLog execution remains required before release claims about live channel availability, permissions, cancellation cost or retained Event 101 payload compatibility.
