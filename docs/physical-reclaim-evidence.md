# Current physical reclaim evidence

## Purpose

SHA-256 equality proves content equality for the fully hashed sampled paths, but it still does not tell FileOp how many **physical bytes** deleting one path could reclaim.

The same explicit Verify-content operation therefore performs a second read-only metadata step for SHA-256 matching sampled paths. While the original read handles remain open, FileOp revalidates:

- current physical file identity;
- current hard-link count;
- current allocated disk bytes.

The result is a **current physical reclaim upper bound**, not an authorized cleanup operation.

## Windows evidence source

`WindowsCurrentFilePhysicalEvidenceReader` receives the already-open read handle created by the bounded content verifier.

It uses two read-only handle queries:

- `GetFileInformationByHandle` for volume serial number, file index, logical file size and hard-link count;
- `GetFileInformationByHandleEx(..., FileStandardInfo, ...)` for allocation size, end-of-file, hard-link count, delete-pending state and directory state.

FileOp combines volume serial + file index into `StoragePhysicalFileIdentity` for the current physical file object.

Allocation, logical length and hard-link evidence therefore come from the same already-open handle set used for content verification. There is no pathname allocation query. FileOp requires both handle-level structures to agree on logical length and hard-link count before trusting the physical reclaim value.

The reader also rejects delete-pending handles and directory handles. This is conservative current evidence, not a transactional filesystem snapshot.

## Evidence validation

Physical reclaim analysis requires one current physical evidence record for every SHA-256 matched sampled path.

FileOp rejects physical reclaim accounting when:

- a matched path has no current evidence;
- an evidence path appears more than once;
- hard-link count is zero;
- the two handle-level queries disagree on hard-link count;
- the two handle-level length views disagree with the open stream;
- allocation is negative;
- the current handle is delete-pending or represents a directory;
- two paths with one physical identity disagree on current link count or allocated bytes;
- the number of sampled aliases for one identity exceeds its current hard-link count;
- one current physical identity appears in more than one SHA-256 match set.

Any such inconsistency produces physical status `Unavailable` and zero verified physical reclaim bytes. Valid SHA-256 content evidence is retained separately.

## Hard-link collapse

Multiple matching sampled paths can be names for the same physical NTFS file.

FileOp collapses equal current `StoragePhysicalFileIdentity` values before physical accounting. Those aliases represent one physical file and are never multiplied into reclaim bytes.

A physical file contributes to the deletion-based reclaim upper bound only when its **current hard-link count is exactly one**.

If hard-link count is greater than one, deleting one sampled name is not sufficient evidence that the file's allocation will be released, because another link remains. FileOp therefore contributes zero reclaim bytes for that physical object.

## Match-set reclaim formula

Each SHA-256 match set is handled independently.

### All unique physical files are singleton-link

At least one content-equivalent physical file must remain. FileOp keeps the singleton physical file with the smallest current allocation in the accounting model and saturating-sums the allocations of the other singleton copies.

This maximizes the possible reclaim while still preserving one copy.

### A non-singleton physical file exists

A multi-link physical file already cannot be counted as reclaimable from deleting one sampled path, so it can serve as the required retained content-equivalent file in the upper-bound model.

FileOp can then saturating-sum all singleton-link physical copies in that match set.

### Fewer than two unique physical files

Reclaim upper bound is zero. This commonly occurs when multiple hash-matching sampled paths are hard-link aliases of the same physical file.

## Saturation

Physical byte totals use saturating arithmetic. Extremely large or malformed aggregate values cannot wrap negative.

For all-singleton sets, FileOp chooses the retained minimum-allocation object **before** summing reclaim candidates. This avoids undercounting caused by saturating a total first and subtracting later.

## Evidence lifetime

Physical identity/link/allocation evidence is captured after content hashes finish and before the verifier closes its bounded read-handle set.

The displayed value becomes stale when those handles close and the files subsequently change. It is therefore labelled a current upper bound and is not persisted as cleanup authority.

## Separation from content evidence

The UI keeps three levels separate:

1. full-group same-size candidate logical upper bound;
2. SHA-256-matched sampled logical duplicate bytes;
3. current physical reclaimable-byte upper bound for those hash-matched sampled paths.

A physical metadata failure affects only level 3. It does not downgrade a successfully completed SHA-256 result.

## Privilege and protocol boundary

This layer remains in the desktop/Windows library path used by content verification.

It does not add a named-pipe operation and protocol remains v8. File contents are not read through an elevated indexing helper. The physical metadata reader performs no write/control/delete operation.

## Destructive-operation boundary

A nonzero current physical reclaim upper bound is **not deletion authorization**.

Before FileOp can offer deletion/deduplication, a separately reviewed workflow still needs to:

- select explicit user-authorized paths;
- run the existing Files preflight/recovery/authorization boundary;
- revalidate current identity/content/allocation assumptions at action time;
- avoid deleting required hard-link names or protected/system content;
- preserve undo/recovery evidence where supported.

This PR adds evidence only.

## Validation without GitHub Actions

`tools/verify_physical_reclaim_evidence.py` is wired into `tools/test-local.ps1 -OfflineOnly`.

Its deterministic randomized model covers:

- singleton versus multi-link physical files;
- retaining one copy;
- hard-link alias zero-reclaim cases;
- singleton copies beside an already-retained multi-link object;
- saturated allocation totals.

Source guards pin handle-only identity/link/allocation metadata, dual length/link cross-checks, delete-pending/directory rejection, post-hash ordering, unchanged protocol v8, UI uncertainty wording and the absence of write/delete/control APIs.

Focused .NET tests cover portable Core accounting plus a Windows-only reader regression that requires two open handles to the same temporary file to report the same current identity/allocation evidence.

Native Windows/.NET execution is not claimed from the current sandbox.
