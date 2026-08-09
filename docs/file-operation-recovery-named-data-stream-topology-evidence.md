# Copy recovery named-data-stream topology evidence

## Purpose

The primary/default stream SHA-256 evidence covers only the unnamed data stream. Windows files can also contain named `$DATA` streams (commonly called alternate data streams). Deleting a file deletes those streams too, so their existence cannot be ignored when building conservative recovery evidence.

This slice records and later compares **named-stream topology/size evidence**. It does not read or hash named-stream contents.

## Handle-bound enumeration

Windows enumeration uses `GetFileInformationByHandleEx` with `FileStreamInfo` and parses the returned `FILE_STREAM_INFO` chain from an already verified file handle.

The parser:

- requires the unnamed/default `::$DATA` entry;
- accepts named entries only in `:name:$DATA` form;
- rejects malformed offsets, odd/invalid UTF-16 lengths, negative stream sizes, duplicate names, unexpected stream types, implausible stream counts, and inventories larger than the configured safety bound;
- treats an unsupported/no-inventory result as unavailable rather than assuming there are no named streams.

No pathname-based `FindFirstStreamW`/`FindNextStreamW` enumeration is used.

## Canonical digest

Raw stream names are not persisted and malformed-inventory diagnostics do not echo a raw stream name.

The current format is `FileNamedDataStreamTopologyEvidence.CurrentFormatVersion == 1`. The Windows helper sorts named stream entries by exact returned stream name (`StringComparer.Ordinal`) and hashes a deterministic binary sequence containing:

1. a fixed format prefix;
2. the named-stream count;
3. for each sorted named stream: UTF-8 byte length, exact UTF-8 name bytes, and signed 64-bit logical `StreamSize` in little-endian form.

Only the following durable fields are stored:

- format version;
- named-stream count;
- 64-character SHA-256 hex digest.

The action-history database never stores raw named-stream names or named-stream bytes.

## What equality means

`SameNamesAndSizes` means only that the observed named `$DATA` stream names and logical sizes produced the same versioned digest and count.

It does **not** mean named-stream contents are equal.

For example, changing an ADS from four bytes `AAAA` to four different bytes `BBBB` intentionally leaves this topology/size evidence unchanged. Resizing the stream, adding/removing a stream, or changing its name changes the evidence.

This limitation is covered by native regressions so the result cannot quietly drift into an ADS-content claim.

## Commit-bound observation

`WindowsRootBoundFileCommitNamedDataStreamTopologyEvidenceSource` runs while the Copy mutation lease is still alive. It opens the destination leaf relative to the recorded destination-root handle, verifies root/leaf path, ordinary type, non-reparse state and `FileIdentity`, then samples on the **same leaf handle**:

- hard-link count;
- basic metadata;
- owner/group/DACL digest;
- named-data-stream topology/size digest.

The stable fields, security digest and named-stream topology are sampled twice. Any change before the second sample fails the evidence collection.

The leaf handle requests metadata/security access only; no named-stream content read and no mutation/delete access is requested.

## Atomic persistence

`SqliteFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore` adds the schema-v1 side table `file_operation_action_entry_named_data_stream_topology_evidence` without a schema-version bump.

The strongest Copy commit/recovery transition atomically writes:

1. action-entry state + destination identity;
2. primary-stream SHA-256;
3. hard-link count;
4. basic metadata;
5. owner/group/DACL digest;
6. named-stream topology version/count/digest.

The five evidence inserts execute inside the same SQLite transaction as the action-entry state transition. A failure on the final named-stream insert must roll back the entry transition and all earlier evidence rows.

Legacy/security-only histories remain valid and simply have no named-stream topology row.

## Failed-commit recovery provenance

`WindowsFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore` preserves the existing Copy executor call shape.

A failed durable commit may reuse the cached combined topology observation only when recovery supplies the exact destination `FileIdentity` and primary-stream fingerprint from that same commit attempt. A missing or mismatched pair downgrades to the evidence-insufficient legacy recovery transition rather than persisting a partial/mixed proof.

## Current recovery comparison

`WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader` reopens the recorded root and leaf through root-bound handles, verifies root/leaf path/type/reparse/identity, queries topology twice, and revalidates the root before success.

`FileOperationRecoveryNamedDataStreamTopologyVerifier` first reloads durable action history and proves that the supplied recovery inspection still represents the durable operation/root/entry. Only then may it trust the named-stream evidence side table or invoke the filesystem reader.

Results are separate:

- `NoRecordedEvidence`;
- `SameNamesAndSizes`;
- `DifferentNamesOrSizes`;
- `Unavailable`.

## Aggregate recovery evidence

The conservative aggregate assessor adds `NamedDataStreams` as its seventh dimension:

- `SameNamesAndSizes` -> `Matches`;
- `DifferentNamesOrSizes` -> `Changed`;
- `NoRecordedEvidence` -> `Incomplete`;
- `Unavailable` -> `Unavailable`.

The existing aggregate precedence remains changed > unavailable > incomplete > observed-subset-match.

Even when all seven dimensions match, `ObservedSubsetMatches` is still **not an unchanged-file result** because same-name/same-size named-stream content changes are outside this slice.

## Safety boundary

This slice grants **no mutation authority**. It does not:

- open or read named-stream contents;
- create, delete, rename, truncate, or write named streams;
- change `UndoKind`;
- add `CanDelete` or `CanUndo`;
- delete, replace, move, or recover a destination;
- wire Files UI Run/Undo.

Extended attributes, complete hard-link-name topology, SACL/audit state, named-stream content equality, and final destructive policy remain outside scope.

Any future destructive authorization still requires explicit user authorization and fresh handle-bound revalidation held through the actual relative mutation.