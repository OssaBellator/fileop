# Same-size content verification

## Purpose

Storage Optimize uses exact logical file size as a cheap duplicate prefilter. Equal size alone is not content equality.

This slice adds an explicit **Verify content** action for one same-size candidate group. It is lazy, bounded and read-only: FileOp hashes only a bounded subset of the paths already sampled by the native index and does not run content reads during ordinary Optimize refresh.

## Privilege boundary

Content verification deliberately stays out of `FileOp.Indexer` and indexing-service protocol v8.

The native helper may have been started with administrative indexing access, but that does not grant the desktop an arbitrary elevated content-read API. Verification opens candidate paths directly from the desktop process under the interactive user's access token.

If the user cannot read a file, or another process holds it with incompatible sharing, the group remains unverified.

## Scope validation

The desktop accepts verification only for an existing in-memory `StorageSameSizeCandidateGroup` from the current Optimize analysis.

Before reading content it requires:

- the current Storage root to still match the analysis root;
- every sampled path to remain inside that root;
- every sampled candidate to still declare the group's indexed logical length;
- at least two distinct sampled paths.

A source change makes the old result stale. The MainWindow discards it and resumes loading the current source after the bounded verifier exits.

## Read budget

`StorageSameSizeContentVerificationPolicy.Default` allows:

- at most **8** sampled files;
- at most **2 GiB** of file content supplied to FileOp's hash loop;
- a 1 MiB hash buffer.

The hard maximum read budget is also 2 GiB in this slice.

FileOp selects only whole files. The selected count is therefore:

`min(sample count, max files, floor(byte budget / logical bytes per file))`.

If fewer than two complete files fit, verification returns `BudgetLimited` **before opening any path**. Partial-file hashes never become duplicate evidence.

`BytesRead` is the number of file-content bytes returned to FileOp and supplied to SHA-256. It is not a physical-device I/O counter: Windows cache, filesystem buffering and device read-ahead may make physical I/O differ.

## Open/share behavior

All selected files are opened before hashing starts with:

- `FileAccess.Read`;
- `FileShare.Read` only;
- asynchronous + sequential-scan options.

This means FileOp requests a read-only handle set that does not share write or delete access. Normal Windows opens with incompatible existing write/delete access fail instead of being hashed concurrently.

FileOp checks every opened stream length against the indexed same-size length before hashing and checks length again after each full hash. A length mismatch returns `CandidateChanged` and discards partial match evidence.

This sharing discipline is intentionally conservative, but it is not presented as a filesystem transaction or an absolute guarantee against every exotic modification mechanism. The result is described as content-hash evidence.

## Hash semantics

Each selected file is fully processed with SHA-256. FileOp groups only equal full-file digests.

The result exposes matching **path sets**, not raw digest values. A set with two or more paths contributes verified logical duplicate bytes:

`logical bytes per file × (matching path count - 1)`.

Only fully hashed selected paths contribute. Candidate paths that were not selected because of the file-count/byte budget remain outside verified savings.

A completed sample with no matching SHA-256 digests means only that no content-hash match was found among the fully hashed sample. It does not prove the larger candidate group has no duplicates.

## Logical evidence versus physical reclaim

The index still shows `PotentialLogicalSavingsUpperBound` for the complete same-size candidate group. That number remains a cheap candidate upper bound.

The verifier separately shows `VerifiedLogicalDuplicateBytes` for matching fully hashed sampled paths.

Neither value is called verified physical reclaimable space in this slice. Between the index snapshot and content verification, physical file identity, allocation/compression/sparse state or hard-link topology can change. A later physical-reclaim boundary must revalidate those properties before presenting reclaimable bytes or authorizing deletion.

## UI coordination

Only one same-size verification runs at a time.

While it is active FileOp disables:

- Optimize refresh;
- ordinary Performance diagnostics refresh;
- explicit Disk I/O capture;
- other same-size verification buttons.

This prevents a large hash read from contaminating FileOp's foreground latency or ETW measurements and prevents multiple verification reads from competing with each other.

Performance/Disk-I/O work can run normally again after verification completes or fails.

## Failure states

The result distinguishes:

- `Completed` — selected files were fully hashed; matching sets may be empty or present;
- `BudgetLimited` — fewer than two whole files fit, so no content is read;
- `CandidateChanged` — a selected file no longer has the indexed same-size length;
- `Unavailable` — paths could not all remain readable under the requested sharing/access mode.

Cancellation propagates rather than becoming a verification result.

## Deliberate non-goals

This slice does not:

- read contents from the elevated indexer/helper;
- change protocol v8;
- hash every same-size group automatically;
- run periodic/background duplicate scans;
- persist content hashes;
- send hashes or paths anywhere;
- claim cryptographic digest equality is physical reclaim evidence;
- delete, move, hard-link or deduplicate files;
- bypass Files preflight/recovery/authorization;
- call a same-size candidate a confirmed duplicate before explicit full-file hash evidence exists.

## Validation without GitHub Actions

`tools/verify_same_size_content_verification.py` is part of `tools/test-local.ps1 -OfflineOnly`.

The deterministic randomized model covers whole-file budget selection, candidate versus sampled scope, matching-set accounting and verified logical-byte bounds. Source guards require read-only/share-read handles, SHA-256 full-stream hashing, path containment, explicit UI invocation, measurement mutual exclusion and unchanged protocol v8.

Focused Windows/.NET tests cover:

- two full-file matches plus a same-size last-byte difference;
- budget refusal before touching nonexistent paths;
- indexed length drift before hashing;
- incompatible writer sharing;
- exact whole-file budget selection;
- pre-cancellation before path access.

Native Windows/.NET execution is not claimed from the current sandbox.
