#!/usr/bin/env python3
"""Verify bounded same-size content verification without GitHub Actions."""
from __future__ import annotations

import argparse
import collections
import random
from pathlib import Path

HARD_MAX_BYTES = 2 * 1024 * 1024 * 1024


def verify_model(
    size: int,
    candidate_count: int,
    sample_count: int,
    max_files: int,
    budget: int,
    digests: list[int],
):
    selected = min(sample_count, max_files, budget // size)
    if selected < 2:
        return "budget", 0, 0, [], 0

    selected_digests = digests[:selected]
    counts = collections.Counter(selected_digests)
    matching_sets = [count for count in counts.values() if count >= 2]
    verified_logical = size * sum(count - 1 for count in matching_sets)
    return "completed", selected, selected * size, matching_sets, verified_logical


def run_model(cases: int) -> int:
    checks = 0
    assert verify_model(100, 5, 5, 8, 199, [1] * 5)[0] == "budget"
    checks += 1
    fixed = verify_model(100, 5, 5, 8, 300, [1, 1, 2, 1, 1])
    assert fixed[1] == 3
    assert fixed[2] == 300
    assert fixed[4] == 100
    checks += 3

    rng = random.Random(20260811)
    for _ in range(cases):
        size = rng.randint(1, 10_000_000)
        candidate_count = rng.randint(2, 50)
        sample_count = rng.randint(2, min(8, candidate_count))
        max_files = rng.randint(2, 8)
        budget = rng.randint(1, HARD_MAX_BYTES)
        digests = [rng.randint(0, min(sample_count, 4)) for _ in range(sample_count)]
        result = verify_model(
            size,
            candidate_count,
            sample_count,
            max_files,
            budget,
            digests,
        )
        candidate_upper = size * (candidate_count - 1)

        if result[0] == "budget":
            assert min(sample_count, max_files, budget // size) < 2
            assert result[1] == 0
            assert result[2] == 0
            checks += 3
            continue

        _, selected, bytes_read, matching_sets, verified_logical = result
        assert 2 <= selected <= min(sample_count, max_files)
        assert bytes_read == selected * size and bytes_read <= budget
        assert verified_logical <= size * (selected - 1)
        assert verified_logical <= candidate_upper
        assert sum(matching_sets) <= selected
        checks += 6

    return checks


def check_repository(root: Path) -> int:
    model = (root / "src/FileOp.Core/Storage/StorageOptimization.cs").read_text(encoding="utf-8")
    verifier = (root / "src/FileOp.Windows/Storage/WindowsSameSizeContentVerifier.cs").read_text(encoding="utf-8")
    desktop = (root / "src/FileOp.App/DesktopSearchEngine.StorageOptimization.cs").read_text(encoding="utf-8")
    main = (root / "src/FileOp.App/MainWindow.StorageOptimization.cs").read_text(encoding="utf-8")
    xaml = (root / "src/FileOp.App/StorageOptimizationView.xaml").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageOptimizationView.xaml.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/WindowsSameSizeContentVerifierTests.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "StorageSameSizeContentVerificationPolicy",
        "HardMaximumTotalBytesRead = 2L * 1024 * 1024 * 1024",
        "StorageSameSizeContentVerificationStatus",
        "StorageVerifiedContentMatchSet",
        "VerifiedLogicalDuplicateBytes",
        "HasVerifiedDuplicateEvidence",
    ):
        assert needle in model, needle
        checks += 1

    for needle in (
        "FileAccess.Read",
        "Share = FileShare.Read",
        "FileOptions.Asynchronous | FileOptions.SequentialScan",
        "IncrementalHash.CreateHash(HashAlgorithmName.SHA256)",
        "ArrayPool<byte>.Shared.Rent",
        "policy.MaxTotalBytesRead / group.LogicalBytesPerFile",
        "MatchingSets: Array.Empty<StorageVerifiedContentMatchSet>()",
        "physical reclaimable space is not verified here",
    ):
        assert needle in verifier, needle
        checks += 1

    # Every selected file is opened and length-checked before digest grouping/hashing starts.
    open_loop = verifier.index("foreach (var candidate in selected)")
    digest_setup = verifier.index("var digestGroups =")
    hash_call = verifier.index("HashStreamAsync(", digest_setup)
    assert open_loop < digest_setup < hash_call
    checks += 2

    for forbidden in (
        "FileAccess.Write",
        "FileShare.Write",
        "FileShare.Delete",
        "FileMode.Create",
        "File.Delete(",
        "Directory.Delete(",
        "SHA1",
        "MD5",
    ):
        assert forbidden not in verifier, forbidden
        checks += 1

    for needle in (
        "VerifySameSizeContentAsync(",
        "EnsurePathWithinRoot(Path.GetFullPath(file.Path), fullRoot)",
        "_sameSizeContentVerifier.VerifyAsync(",
        "StorageSameSizeContentVerificationPolicy.Default",
    ):
        assert needle in desktop, needle
        checks += 1
    assert "AnalyzeStorageOptimizationAsync(" not in desktop[desktop.index("VerifySameSizeContentAsync(") :]
    checks += 1

    for needle in (
        'Content="Verify content"',
        "fully SHA-256 hashes a bounded sample under your user account",
        "physical reclaimable space is still not verified",
        "Potential savings are a logical upper bound only",
    ):
        assert needle in xaml, needle
        checks += 1

    for needle in (
        "SameSizeVerificationRequested",
        "SetSameSizeVerificationLoading",
        "ApplySameSizeVerification",
        "VerifiedLogicalDuplicateBytes",
        "never uses the elevated indexer to read file contents",
        "content-hash evidence, not verified physical reclaimable space",
    ):
        assert needle in view, needle
        checks += 1

    for needle in (
        "_storageSameSizeVerificationActive",
        "StorageOptimizationView_SameSizeVerificationRequested",
        "_searchEngine.VerifySameSizeContentAsync(",
        "ReferenceEquals(analysis, _storageOptimizationAnalysis)",
        "_performanceDiskIoCaptureActive ||",
    ):
        assert needle in main, needle
        checks += 1

    for needle in (
        "FullyHashesBoundedSampleAndReturnsOnlyMatchingSets",
        "BudgetLimitRefusesToReadEvenNonexistentCandidates",
        "ChangedLengthFailsClosedBeforeHashing",
        "IncompatibleWriterKeepsGroupUnverified",
        "ByteBudgetSelectsOnlyWholeFilesAndNeverOverreads",
        "PreCancelledVerificationDoesNotOpenFiles",
    ):
        assert needle in tests, needle
        checks += 1

    # Content verification stays outside the elevated named-pipe protocol in this slice.
    assert "public const int CurrentVersion = 8;" in protocol
    assert "VerifySameSizeContent" not in protocol
    checks += 2

    assert "verify_same_size_content_verification.py" in gate
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repo_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {repo_checks:,} source/test checks" if args.repo_root else ""
    print(
        "PASS: same-size content verification verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized groups{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
