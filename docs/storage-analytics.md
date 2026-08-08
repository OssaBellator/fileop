# FileOp storage analytics

## Purpose

Storage analytics consumes FileOp's existing filesystem metadata index. It must not recursively rescan the filesystem merely to answer folder-size or treemap questions that the shared index can already answer.

The first analytics contract is `IStorageAnalytics.AnalyzeDirectoryAsync`. It returns a bounded list of direct entries under one directory plus complete totals for that directory's indexed subtree.

## Size semantics

FileOp distinguishes namespace size from physical disk usage:

- `LogicalBytes` sums the logical length of every file name in the analyzed namespace. Hard-linked names therefore each contribute logical bytes because each name represents a visible file entry.
- `AllocatedBytes` represents physical allocation and counts each stable `FileIdentity` at most once inside the analyzed root.
- `AllocatedBytes` is nullable. If any physical file that owns allocation in an aggregate lacks allocated-size metadata, the aggregate remains unknown rather than substituting logical bytes and presenting an estimate as exact physical usage.
- `TreemapBytes` is `AllocatedBytes ?? LogicalBytes`. This gives rendering a usable per-entry weight while preserving the distinction in the data model.

Files without a stable provider identity are treated as unique. That is the correct conservative behavior for crawler/fallback providers that cannot prove two paths refer to the same physical file.

## Hard links

NTFS hard links create multiple namespace rows for one physical file. Naively summing `allocated_length` for every row would overstate disk usage and could make child treemap areas add up to more than the analyzed root.

Within one analysis request, FileOp chooses a deterministic canonical path for each repeated `FileIdentity`: the case-insensitive lexicographically first indexed path inside the analyzed scope. Physical allocation is attributed to that path's direct-child branch exactly once. Other namespace rows remain in `LogicalBytes` and `FileCount` but increment `HardLinkAliasCount` instead of consuming allocation again.

This produces these invariants:

```text
UniqueFileCount = FileCount - HardLinkAliasCount
sum(direct child allocated bytes) = root allocated bytes   // when allocation is fully known
```

A directory containing only a non-canonical hard-link alias can therefore have non-zero logical bytes but zero allocated bytes for this analysis. That is intentional: the directory exposes a name for data whose physical allocation has already been attributed elsewhere in the same analyzed scope.

Canonical attribution is a presentation/accounting rule, not an assertion that one hard-link name owns the physical file more strongly than another.

## Directory aggregation

A request for `C:\Data` returns the direct children of `C:\Data`. For a direct directory such as `C:\Data\Projects`, its row recursively includes all indexed descendants below `Projects`.

Each entry reports:

- path and display name;
- file/directory identity of the entry itself (`IsDirectory`);
- logical bytes;
- nullable physical allocated bytes;
- namespace file count;
- unique physical file count derived from hard-link aliases;
- directory count;
- hard-link alias count.

Directory counts include the direct directory itself plus descendant directories. Root totals exclude the analyzed root itself and summarize the returned direct-entry subtrees.

`MaxEntries` limits only the direct-entry list. Root totals and `DirectEntryCount` are calculated before truncation so the UI can display exact totals while rendering only the largest entries.

## SQLite implementation

`SqliteStorageAnalytics` operates directly on the `files` table used by persistent search. A recursive CTE walks descendants from the direct-child rows. A window rank over stable file identity selects one physical-allocation owner for hard-linked names before per-child aggregation.

The query is read-only. The indexing service acquires the existing process-local volume operation gate and a shared cross-process volume-index lease before checking the durable NTFS checkpoint and executing analytics. Snapshot rebuild and journal synchronization hold the exclusive lease, so analytics cannot observe a partially rebuilt namespace.

An additive `parent_path` index accelerates recursive child lookup. It does not change the persisted row shape or schema-version contract.

## In-memory fallback

`InMemoryFileIndex` implements the same `IStorageAnalytics` contract over the completed crawler snapshot. It groups descendants by direct child and applies the same canonical hard-link rule whenever stable identities are present.

The normal crawler currently does not supply stable NTFS identities or allocated sizes, so fallback results generally report logical size and unknown allocation. The UI preserves that distinction rather than claiming the fallback snapshot knows exact physical usage.

The desktop does not run another fallback crawl when the user opens Storage. It reuses the same in-memory snapshot already built for Search. The available fallback root is therefore the user profile, not the entire drive, and the Storage page labels that scope explicitly.

## Protocol

Indexing protocol v2 adds `AnalyzeStorage` with:

```text
VolumeIdentity
VolumeRootPath
DirectoryPath
MaxEntries
```

The helper verifies that the requested physical volume/root pair exists, the target directory remains inside that root, and a durable checkpoint is valid before serving data. Invalid snapshots return `SnapshotRequired`; maintenance contention returns retryable `Busy`.

The operation is read-only. It does not add cleanup, deletion, partition, formatting, BitLocker, or other destructive capabilities to `FileOp.Indexer`.

## WinUI consumption

`DesktopSearchEngine` is the desktop's shared foreground coordinator for both Search and Storage. Storage does not start a second helper session and does not open the SQLite database directly.

In native mode, the coordinator exposes the root of the indexed NTFS volume containing the user profile and forwards directory analysis to protocol v2 `AnalyzeStorage`. In fallback mode, it exposes the already-indexed user-profile root and forwards analysis to the same `InMemoryFileIndex` used by Search.

Search and Storage both pass through the coordinator's foreground operation gate. This matters because the reviewed native-to-fallback transition waits that gate before disposing the active helper session. A storage read therefore cannot race helper teardown any more than a search request can.

The WinUI Storage page provides:

- whole-root logical/on-disk/unique-file/hard-link summary cards;
- Up and Refresh navigation;
- click-through folder drill-down;
- a detailed direct-entry list;
- a proportional binary treemap of the largest returned entries;
- an `Other entries` tile when more than 48 positive-weight entries are present.

A newer Storage navigation request supersedes the visible generation but does not cancel an already-transmitted IPC exchange. This mirrors Search's reviewed request/response rule: interrupting an exchange faults that helper connection, so supersession is handled by discarding stale work instead. Window shutdown may cancel the active exchange because the entire helper session is being destroyed.

### Treemap weighting

If the analyzed root has complete `AllocatedBytes`, tile area represents hard-link-deduplicated physical allocation.

If root allocated size is unknown, the UI switches **all** tile weights to logical bytes for that render. It does not mix known physical allocation for some branches with logical fallback for others, because that would produce a visually non-additive treemap with no coherent root total.

Entries whose physical allocation is zero because they contain only non-canonical hard-link aliases may therefore disappear from a physical treemap while remaining visible in the detailed list. That is expected: the list describes namespace entries, while the physical treemap describes disk allocation.

## Performance validation

`StorageAnalyticsBenchmarks` seeds deterministic 100,000- and 1,000,000-file SQLite indexes and measures both root-level and project-subtree aggregation. The benchmark project is compiled in normal CI while long BenchmarkDotNet runs remain manual.

The current recursive SQLite implementation is the correctness/reference baseline, not an assumption that it is the final WizTree-class aggregation engine. If measurements require it, FileOp can later maintain incremental directory aggregates or another specialized structure while retaining the same `IStorageAnalytics` semantics.
