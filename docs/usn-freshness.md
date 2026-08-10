# USN checkpoint freshness evidence

## Purpose

FileOp should be able to explain whether its native NTFS index is merely present or whether its durable journal checkpoint is still continuous with the live NTFS USN journal.

This evidence is diagnostic only. It does not advance the checkpoint, read journal records, trigger synchronization, rebuild the index or assign a health score.

## Durable evidence

The existing per-volume SQLite database stores a durable source checkpoint:

```text
JournalId     // checkpoint generation
NextUsn       // next USN position FileOp expects to read
UpdatedAt     // UTC timestamp of the durable checkpoint write
```

Protocol v8 index diagnostics read those fields from `source_checkpoints` through the same read-only/query-only SQLite connection used for index database evidence.

The checkpoint timestamp is displayed as **checkpoint age** relative to the diagnostics capture time. Age is descriptive only: a recent timestamp does not prove journal continuity, and an older timestamp does not prove that files changed.

## Live USN evidence

When raw journal access is available, the helper performs one metadata query with `NtfsUsnJournal.Query(volume)`. It does not call the journal record reader.

The live state contributes:

```text
JournalId
LowestValidUsn
NextUsn
```

`LowestValidUsn` is the current retention floor. `NextUsn` is the live journal head position.

Live journal evidence is optional. Access can be unavailable, including when a stale-but-readable native index remains usable without the privileges required for a fresh raw journal query. In that case FileOp keeps the durable checkpoint and SQLite evidence and labels live freshness as unavailable.

## Continuity states

FileOp reports one of four evidence states when live journal metadata is available.

### Readable window

The durable checkpoint is incrementally readable only when:

```text
DurableJournalId == LiveJournalId
LowestValidUsn <= DurableNextUsn <= LiveNextUsn
```

Only in this state does FileOp calculate backlog and retention-headroom distances.

### Journal identity changed

If the durable and live journal IDs differ, FileOp does not calculate a backlog. The two positions belong to different journal generations and cannot be treated as one continuous sequence.

### Below retention floor

If the journal IDs match but:

```text
DurableNextUsn < LowestValidUsn
```

then the range between FileOp's checkpoint and the current retention floor is no longer readable from the journal. FileOp reports that incremental replay cannot cover the missing range; it does not invent a backlog number for an invalid window.

### Checkpoint ahead of journal

If the journal IDs match but:

```text
DurableNextUsn > LiveNextUsn
```

FileOp reports an inconsistency rather than treating the index as current or clamping the difference to zero.

## USN distances

For a checkpoint inside the readable window:

```text
BacklogUsnDistance = LiveNextUsn - DurableNextUsn
RetentionHeadroomUsnDistance = DurableNextUsn - LowestValidUsn
```

These are **USN sequence-position differences**. They are not:

- file counts;
- filesystem-event counts;
- changed-byte counts;
- journal-file byte sizes;
- elapsed time;
- estimated synchronization duration.

A single filesystem operation may emit multiple journal records, USN positions need not map one-to-one to user-visible changes, and FileOp does not read the intervening records merely to populate this diagnostics view.

When continuity is valid, the two distances satisfy:

```text
BacklogUsnDistance + RetentionHeadroomUsnDistance
    == LiveNextUsn - LowestValidUsn
```

This arithmetic invariant is covered by the portable randomized verifier.

## Protocol compatibility

This slice remains protocol v8. `IndexDatabaseDiagnostics` gains optional trailing `DurableCheckpoint` and `JournalFreshness` payloads.

Older v8 helpers can omit those fields; the desktop displays the existing database evidence and labels checkpoint freshness unavailable. Older v8 desktops ignore the additional JSON properties. No new operation is required because the data extends the existing read-only `GetIndexDiagnostics` response.

## Foreground and safety boundary

USN freshness is collected only during the explicit Performance diagnostics flow. There is no new timer, background poller or recursive scan.

The provider:

- uses the existing attached-volume scope;
- uses the existing shared cross-process index read lease;
- reads the durable checkpoint from SQLite without mutation;
- performs at most one live journal metadata query;
- does not call `FSCTL_READ_USN_JOURNAL` for this feature;
- does not save/delete/advance a checkpoint;
- does not trigger synchronization or rebuild;
- does not change journal capacity or retention policy.

A live journal query failure does not discard already-valid database, checkpoint, Search latency, Storage latency or free-space evidence.

## Validation without GitHub Actions

`tools/verify_usn_freshness.py` models journal identity, retention floor, live head and durable checkpoint positions across randomized cases. It requires backlog/headroom to remain unknown whenever continuity is invalid and proves the readable-window distance invariant when continuity is valid.

Repository mode additionally guards:

- read-only durable checkpoint loading;
- one live `NtfsUsnJournal.Query` metadata observation;
- no journal record reads or checkpoint mutation;
- UI wording that preserves USN units;
- changed-generation, expired-retention and ahead-of-head presentation;
- typed protocol-v8 round-trip preservation of optional freshness evidence.

Focused .NET tests cover durable unsigned journal identity/timestamp persistence and the domain continuity states. Native Windows execution remains part of the local no-Actions Windows gate.
