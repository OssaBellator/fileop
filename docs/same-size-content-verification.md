# Same-size content verification

## Purpose

Storage Optimize uses exact logical file size as a cheap duplicate prefilter. Equal size alone is not content equality.

The explicit **Verify content** action is lazy, bounded and read-only: FileOp hashes only a bounded subset of paths already sampled by the native index and does not run content reads during ordinary Optimize refresh.

For SHA-256 matching sampled paths, the same operation now performs a second, metadata-only physical-evidence step while the original read handles are still open. That step revalidates current physical identity, hard-link count and allocated disk bytes before FileOp displays a physical reclaim upper bound.

## Privilege boundary

Content and physical verification stay out of `FileOp.Indexer` and indexing-service protocol v8.

The native helper may have been started with administrative indexing access, but that does not grant the desktop an arbitrary elevated content-read API. Verification opens candidate paths directly from the desktop process under the interactive user's access token.

If the user cannot read a file, or another process holds it with incompatible sharing, the group remains content-unverified. If content hashes succeed but current physical metadata cannot be obtained, SHA-256 evidence remains valid while physical reclaim stays separately unavailable.

## Scope validation

The desktop accepts verification only for an existing in-memory `StorageSameSizeCandidateGroup` from the current Optimize analysis.

Before reading content it requires:

- the current Storage root to still match the analysis root;
- every sampled path to remain inside that root;
- every sampled candidate to still declare the group's indexed logical length;
- at least two distinct sampled paths.

A real source/root change makes the old result stale. MainWindow discards it and resumes loading the current source after the bounded verifier exits. A transient same-source index-busy state does not erase an active verification because the content/physical read path does not use the indexer.

## Read budget

`StorageSameSizeContentVerificationPolicy.Default` allows:

- at most **8** sampled files;
- at most **2 GiB** of file content supplied to FileOp's hash loop;
- a 1 MiB hash buffer.

The hard maximum content budget is also 2 GiB.

FileOp selects only whole files:

`min(sample count, max files, floor(byte budget / logical bytes per file))`.

If fewer than two complete files fit, verification returns `BudgetLimited` **before opening any path**. Partial-file hashes never become duplicate evidence.

`BytesRead` counts content bytes supplied to SHA-256. It is not a physical-device I/O counter: Windows cache, filesystem buffering and device read-ahead can make device I/O differ.

## Open/share behavior

All selected files are opened before hashing starts with:

- `FileAccess.Read`;
- `FileShare.Read` only;
- asynchronous + sequential-scan options.

FileOp therefore requests a read-only handle set that does not share write or delete access. Normal Windows opens with incompatible existing write/delete sharing fail instead of being hashed concurrently.

Every opened stream length must equal the indexed same-size length before hashing, and length is checked again after each full hash. A mismatch returns `CandidateChanged` and discards partial match evidence.

This sharing discipline is conservative, but it is not a filesystem transaction or an absolute guarantee against every exotic modification mechanism. Results are current evidence and become stale if files change after the handles close.

## Hash semantics

Each selected file is fully processed with SHA-256. FileOp groups only equal full-file digests.

The result exposes matching **path sets**, not raw digest values. A set with two or more paths contributes verified logical duplicate bytes:

`logical bytes per file × (matching path count - 1)`.

Only fully hashed selected paths contribute. Candidate paths not selected because of the file-count/content budget remain outside verified logical savings.

A completed sample with no SHA-256 match means only that no content match was found among the fully hashed sample. It does not prove the larger same-size group contains no duplicates.

## Three evidence levels

### 1. Same-size candidate upper bound

`PotentialLogicalSavingsUpperBound` is computed from the full indexed same-size candidate group. It remains a cheap logical **candidate** upper bound.

### 2. Hash-matched logical duplicate bytes

`VerifiedLogicalDuplicateBytes` uses only fully hashed sampled paths in SHA-256 matching sets.

### 3. Current physical reclaim upper bound

For matching sampled paths, FileOp revalidates current physical identity, hard-link count and allocated disk bytes before exposing `VerifiedPhysicalReclaimableBytesUpperBound`.

That value is a current maximum upper bound for the sampled matches. It assumes one content-equivalent physical file remains in each match set and counts only physical files whose current hard-link count is exactly one as deletable reclaim candidates.

It is **not deletion authorization**. A later cleanup action must still pass the Files preflight/recovery/authorization boundary and revalidate destructive-operation assumptions.

See `docs/physical-reclaim-evidence.md` for the physical accounting rules.

## UI coordination

Only one same-size verification runs at a time.

While it is active FileOp disables:

- Optimize refresh;
- ordinary Performance diagnostics refresh;
- explicit Disk I/O capture;
- other same-size verification buttons.

This prevents a large hash workload from contaminating FileOp's foreground latency/ETW measurements and prevents multiple content reads from competing with each other.

## Failure states

The content result distinguishes:

- `Completed` — selected files were fully hashed; matching sets may be empty or present;
- `BudgetLimited` — fewer than two whole files fit, so no content is read;
- `CandidateChanged` — a selected file no longer has the indexed same-size length;
- `Unavailable` — paths could not all remain readable under the requested sharing/access mode.

A completed content result can additionally carry physical evidence with status `Verified`, `Unavailable` or `NotApplicable`. A physical-metadata failure does not erase valid content-hash evidence.

Cancellation propagates rather than becoming a verification result.

## Deliberate non-goals

This path does not:

- read contents from the elevated indexer/helper;
- change protocol v8;
- hash every same-size group automatically;
- run periodic/background duplicate scans;
- persist content hashes or physical identities;
- send hashes or paths anywhere;
- delete, move, hard-link or deduplicate files;
- bypass Files preflight/recovery/authorization;
- treat current physical evidence as permanent after the handles close.

## Validation without GitHub Actions

`tools/verify_same_size_content_verification.py` and `tools/verify_physical_reclaim_evidence.py` are part of `tools/test-local.ps1 -OfflineOnly`.

The content model covers whole-file budget selection, sampled scope, matching-set accounting and verified logical-byte bounds. The physical model separately covers hard-link identity collapse, singleton-link reclaim rules, keeper selection and saturated byte accounting.

Focused Windows/.NET tests cover content hashing/share/budget behavior plus current-handle identity/allocation consistency. Native Windows/.NET execution is not claimed from the current sandbox.
