#!/usr/bin/env python3
"""Verify read-only known-location cleanup readiness without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

CONSISTENT = "consistent"
CHANGED = "changed"
BLOCKED = "blocked"
UNAVAILABLE = "unavailable"


def analyze(
    *,
    root_binding: bool,
    candidate_binding: bool,
    current_binding: bool,
    root_state: str,
    root_reparse: bool,
    candidate_state: str,
    candidate_reparse: bool,
    same_volume: bool,
    within_root: bool,
    current_present: bool,
    current_reparse: bool,
    same_current_path_identity: bool,
    size_match: bool,
    time_match: bool,
) -> str:
    if not root_binding or not candidate_binding:
        return BLOCKED
    if root_state in {"inaccessible", "error"}:
        return UNAVAILABLE
    if root_state != "directory":
        return CHANGED
    if root_reparse:
        return BLOCKED
    if candidate_state in {"inaccessible", "error"}:
        return UNAVAILABLE
    if candidate_state != "file":
        return CHANGED
    if candidate_reparse or not same_volume or not within_root:
        return BLOCKED
    if not current_present:
        return UNAVAILABLE
    if not current_binding or current_reparse:
        return BLOCKED
    if not same_current_path_identity or not size_match or not time_match:
        return CHANGED
    return CONSISTENT


def exact_review_match_count(
    candidates: list[tuple[str, str, str, str]],
    requested: tuple[str, str, str, str],
) -> int:
    path, root, provenance, rule = requested
    return sum(
        1
        for candidate_path, candidate_root, candidate_provenance, candidate_rule in candidates
        if candidate_path.casefold() == path.casefold()
        and candidate_root.rstrip("\\/").casefold() == root.rstrip("\\/").casefold()
        and candidate_provenance == provenance
        and candidate_rule == rule
    )


def source_binding_current(
    review_identity: int | None,
    start_identity: int | None,
    end_identity: int | None,
    review_root: str,
    start_root: str,
    end_root: str,
) -> bool:
    normalized_review_root = review_root.rstrip("\\/").casefold()
    return (
        review_identity is not None
        and review_identity == start_identity == end_identity
        and normalized_review_root == start_root.rstrip("\\/").casefold()
        and normalized_review_root == end_root.rstrip("\\/").casefold()
    )


def run_model(cases: int, seed: int) -> int:
    checks = 0
    base = dict(
        root_binding=True,
        candidate_binding=True,
        current_binding=True,
        root_state="directory",
        root_reparse=False,
        candidate_state="file",
        candidate_reparse=False,
        same_volume=True,
        within_root=True,
        current_present=True,
        current_reparse=False,
        same_current_path_identity=True,
        size_match=True,
        time_match=True,
    )
    assert analyze(**base) == CONSISTENT
    changed = base | {"within_root": False}
    assert analyze(**changed) == BLOCKED
    changed = base | {"candidate_state": "missing", "current_present": False}
    assert analyze(**changed) == CHANGED
    changed = base | {"same_current_path_identity": False}
    assert analyze(**changed) == CHANGED
    changed = base | {"root_binding": False}
    assert analyze(**changed) == BLOCKED

    overlap = [
        (r"D:\Review\same.zip", r"D:\Review", "downloads", "downloads.old-archive-extension.v1"),
        (r"D:\Review\same.zip", r"D:\Review", "temp", "user-temp.old-large-file.v1"),
        (r"D:\Other\same.zip", r"D:\Other", "downloads", "downloads.old-archive-extension.v1"),
    ]
    assert exact_review_match_count(
        overlap,
        (r"d:\review\same.zip", "d:\\review\\", "temp", "user-temp.old-large-file.v1"),
    ) == 1
    assert exact_review_match_count(
        overlap,
        (r"D:\Review\same.zip", r"D:\Review", "downloads", "user-temp.old-large-file.v1"),
    ) == 0
    assert exact_review_match_count(
        overlap + [overlap[0]],
        overlap[0],
    ) == 2
    assert source_binding_current(10, 10, 10, "C:\\", "c:\\", "C:\\")
    assert not source_binding_current(10, 10, 11, "C:\\", "C:\\", "C:\\")
    assert not source_binding_current(None, 10, 10, "C:\\", "C:\\", "C:\\")
    checks += 11

    rng = random.Random(seed)
    root_states = ("directory", "missing", "file", "inaccessible", "error")
    candidate_states = ("file", "missing", "directory", "inaccessible", "error")
    for index in range(cases):
        values = dict(
            root_binding=rng.random() < 0.97,
            candidate_binding=rng.random() < 0.97,
            current_binding=rng.random() < 0.97,
            root_state=rng.choice(root_states),
            root_reparse=rng.random() < 0.08,
            candidate_state=rng.choice(candidate_states),
            candidate_reparse=rng.random() < 0.08,
            same_volume=rng.random() < 0.94,
            within_root=rng.random() < 0.94,
            current_present=rng.random() < 0.93,
            current_reparse=rng.random() < 0.05,
            same_current_path_identity=rng.random() < 0.92,
            size_match=rng.random() < 0.91,
            time_match=rng.random() < 0.91,
        )
        result = analyze(**values)
        assert result in {CONSISTENT, CHANGED, BLOCKED, UNAVAILABLE}
        expected_consistent = (
            values["root_binding"]
            and values["candidate_binding"]
            and values["root_state"] == "directory"
            and not values["root_reparse"]
            and values["candidate_state"] == "file"
            and not values["candidate_reparse"]
            and values["same_volume"]
            and values["within_root"]
            and values["current_present"]
            and values["current_binding"]
            and not values["current_reparse"]
            and values["same_current_path_identity"]
            and values["size_match"]
            and values["time_match"]
        )
        assert (result == CONSISTENT) == expected_consistent
        if not values["root_binding"] or not values["candidate_binding"]:
            assert result == BLOCKED
        if (
            values["root_binding"]
            and values["candidate_binding"]
            and values["root_state"] in {"inaccessible", "error"}
        ):
            assert result == UNAVAILABLE
        if (
            values["root_binding"]
            and values["candidate_binding"]
            and values["root_state"] == "directory"
            and values["root_reparse"]
        ):
            assert result == BLOCKED
        if (
            values["root_binding"]
            and values["candidate_binding"]
            and values["root_state"] == "directory"
            and not values["root_reparse"]
            and values["candidate_state"] == "file"
            and not values["candidate_reparse"]
            and values["same_volume"]
            and values["within_root"]
            and values["current_present"]
            and not values["current_binding"]
        ):
            assert result == BLOCKED
        if result == CONSISTENT:
            assert (
                values["current_binding"]
                and values["same_current_path_identity"]
                and values["size_match"]
                and values["time_match"]
            )

        # The same path may appear under overlapping known-location rules. Exact
        # root/provenance/rule binding must select only the row the user clicked.
        path = rf"D:\Overlap\candidate-{index % 101}.zip"
        root = r"D:\Overlap"
        requested_provenance = rng.choice(("downloads", "temp"))
        requested_rule = (
            "downloads.old-archive-extension.v1"
            if requested_provenance == "downloads"
            else "user-temp.old-large-file.v1"
        )
        bindings = [
            (path, root, "downloads", "downloads.old-archive-extension.v1"),
            (path, root, "temp", "user-temp.old-large-file.v1"),
        ]
        assert exact_review_match_count(
            bindings,
            (path.swapcase(), root + "\\", requested_provenance, requested_rule),
        ) == 1

        review_identity = rng.randrange(1, 1_000_000)
        start_identity = review_identity if rng.random() < 0.98 else review_identity + 1
        end_identity = start_identity if rng.random() < 0.97 else start_identity + 1
        review_root = "C:\\"
        start_root = "c:\\" if rng.random() < 0.99 else "D:\\"
        end_root = "C:\\" if rng.random() < 0.99 else "D:\\"
        expected_source_current = (
            start_identity == review_identity
            and end_identity == review_identity
            and start_root.rstrip("\\/").casefold() == review_root.rstrip("\\/").casefold()
            and end_root.rstrip("\\/").casefold() == review_root.rstrip("\\/").casefold()
        )
        assert source_binding_current(
            review_identity,
            start_identity,
            end_identity,
            review_root,
            start_root,
            end_root,
        ) == expected_source_current
        checks += 9
    return checks


def require(text: str, needle: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing required source guard: {needle}")
    return 1


def forbid(text: str, needle: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden source token: {needle}")
    return 1


def check_repository(root: Path) -> int:
    checks = 0
    core = (root / "src/FileOp.Core/Storage/StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    review_model = (root / "src/FileOp.Core/Storage/StorageKnownLocationReview.cs").read_text(encoding="utf-8")
    producer = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReview.cs").read_text(encoding="utf-8")
    windows = (root / "src/FileOp.Windows/Storage/WindowsStorageCleanupReadinessService.cs").read_text(encoding="utf-8")
    xaml = (root / "src/FileOp.App/StorageKnownLocationReviewView.xaml").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageKnownLocationReviewView.CleanupReadiness.cs").read_text(encoding="utf-8")
    lifecycle = (root / "src/FileOp.App/StorageKnownLocationReviewView.xaml.cs").read_text(encoding="utf-8")
    coordinator = (root / "src/FileOp.App/MainWindow.StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/StorageCleanupReadinessTests.cs").read_text(encoding="utf-8")
    native_tests = (root / "tests/FileOp.Windows.Tests/WindowsCurrentReviewFileEvidenceReaderTests.cs").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    docs = (root / "docs/cleanup-readiness.md").read_text(encoding="utf-8")

    for needle in (
        "StorageCleanupReadinessStatus",
        "CurrentEvidenceConsistent",
        "CandidateChanged",
        "StorageCleanupCurrentFileEvidence",
        "StorageCleanupReadinessAnalyzer",
        "PathsEqual(canonicalReviewRoot.RequestedPath, reviewRoot)",
        "PathsEqual(canonicalCandidate.RequestedPath, candidate.Path)",
        "canonicalReviewRoot.IsLeafReparsePoint",
        "canonicalCandidate.IsLeafReparsePoint",
        "canonicalReviewRoot.Identity.Value.VolumeSerialNumber",
        "canonicalCandidate.Identity.Value.VolumeSerialNumber",
        "!IsPathWithinRoot(canonicalCandidate.CanonicalPath, canonicalReviewRoot.CanonicalPath)",
        "PathsEqual(currentFile.RequestedPath, candidate.Path)",
        "currentFile.Identity != canonicalCandidate.Identity.Value",
        "currentFile.LogicalBytes != candidate.LogicalBytes",
        "currentFile.LastWriteTimeUtc.UtcDateTime.Ticks !=",
        "candidate.LastWriteTime.ToUniversalTime().UtcDateTime.Ticks",
        "CleanupMutationAuthorized => false",
        "continuity of the same file object since indexing is not proven",
        "This readiness preview does not authorize deletion",
        "must be selected in Files and independently pass the Files recovery",
        "can never be reused as deletion consent or mutation authority",
    ):
        checks += require(core, needle)
    for stale in (
        "The repository does not yet have a durable delete recovery/history executor",
        "Deletion is still unavailable",
        "durable delete recovery/history and final mutation authorization are not implemented",
    ):
        checks += forbid(core, stale)

    checks += require(review_model, "public ulong? ActiveVolumeIdentity { get; init; }")
    for needle in (
        "public ulong? StorageVolumeIdentity",
        "var capturedVolumeIdentity = capturedPrimary.VolumeIdentity;",
        "currentPrimary.VolumeIdentity != capturedVolumeIdentity",
        "ActiveVolumeIdentity = capturedVolumeIdentity",
    ):
        checks += require(producer, needle)

    for needle in (
        "WindowsFileOperationCanonicalPathResolver",
        "dwDesiredAccess: 0",
        "FileShare.ReadWrite | FileShare.Delete",
        "GetFinalPathNameByHandleW",
        "GetFileInformationByHandle",
        "FileAttributes.ReparsePoint",
        "new FileIdentity(",
        "DateTime.FromFileTimeUtc",
        "StorageCleanupReadinessAnalyzer.Analyze",
    ):
        checks += require(windows, needle)
    checks += forbid(windows, "File.GetAttributes")
    for token in (
        "FileAccess.Write",
        "File.Delete(",
        "Directory.Delete(",
        "MoveFileEx",
        "DeleteFileW",
        "DeviceIoControl",
        "SetFileInformationByHandle",
    ):
        checks += forbid(windows, token)

    for needle in (
        'Content="Check readiness"',
        'Click="CheckCleanupReadinessButton_Click"',
        "Neither action prepares, queues, authorizes, or executes deletion",
        "permanent deletion, if later chosen in Files",
    ):
        checks += require(xaml, needle)

    for needle in (
        "StorageKnownLocationCandidateRow row",
        "row.Path",
        "row.ReviewRootPath",
        "row.Provenance",
        "row.RuleId",
    ):
        checks += require(view, needle)
    checks += require(view, "ApplyCleanupReadiness")
    checks += forbid(view, "CleanupMutationAuthorized")

    for needle in (
        "string ReviewRootPath",
        "StorageReviewProvenance Provenance",
        "string RuleId",
        "item.Location.RootPath",
        "candidate.RuleId",
        "No previous readiness result is retained as current evidence",
        "Cleanup readiness has not been checked for this review",
    ):
        checks += require(lifecycle, needle)

    for needle in (
        "string requestedReviewRoot",
        "StorageReviewProvenance requestedProvenance",
        "string requestedRuleId",
        "!await _storageGate.WaitAsync(0)",
        "_performanceDiskIoCaptureActive || _storageSameSizeVerificationActive",
        "_searchEngine.StorageVolumeIdentity is not { } activeVolumeIdentity",
        "location.Provenance != requestedProvenance",
        "!PathsEqual(location.RootPath, requestedReviewRoot)",
        "!PathsEqual(candidate.Path, requestedPath)",
        "candidate.Provenance != requestedProvenance",
        "!string.Equals(candidate.RuleId, requestedRuleId, StringComparison.Ordinal)",
        "matchCount++",
        "matchCount != 1",
        "exact path/root/rule candidate",
        "review.ActiveVolumeIdentity != activeVolumeIdentity",
        "IsPathWithinRoot(matchedCandidate.Path, matchedLocation.RootPath)",
        "_storageCleanupReadinessService.PreviewAsync",
        "ReferenceEquals(review, _storageKnownLocationReview)",
        "currentVolumeIdentity != activeVolumeIdentity",
        "ApplyKnownLocationCleanupReadiness(preview)",
        "_storageGate.Release();",
    ):
        checks += require(coordinator, needle)
    checks += forbid(
        coordinator,
        "IsPathWithinRoot(matchedLocation.RootPath, activeRoot)",
    )

    for needle in (
        "MatchingCurrentEvidenceRemainsNonAuthorizing",
        "IdentityChangeBetweenCurrentReadsFailsClosed",
        "CanonicalEscapeIsBlocked",
        "CandidateReparsePointIsBlocked",
        "CrossVolumeResolutionIsBlocked",
        "CurrentMetadataFailureDoesNotBecomeReady",
        "MismatchedCanonicalRequestedPathsAreBlocked",
        "MismatchedCurrentRequestedPathIsBlocked",
    ):
        checks += require(tests, needle)
    for needle in (
        "ReadAsyncCapturesCurrentHandleIdentitySizeAndLastWrite",
        "ReadAsyncRejectsDirectoryCandidate",
    ):
        checks += require(native_tests, needle)

    checks += require(plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,")
    checks += forbid(plan, "Delete,")
    checks += require(protocol, "public const int CurrentVersion = 8;")
    checks += forbid(protocol, "CleanupReadiness")
    checks += require(gate, "verify_cleanup_readiness.py --repo-root $repoRoot --cases 50000")
    for needle in (
        "now has a separately reviewed Files file-delete session",
        "does **not** prove that this is the same physical file object that was indexed earlier",
        "CleanupMutationAuthorized` is always `false`",
        "not reusable consent or mutation authority",
    ):
        checks += require(docs, needle)
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=20260811)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model = run_model(args.cases, args.seed)
    source = 0
    if not args.self_test_only:
        if args.repo_root is None:
            parser.error("--repo-root is required unless --self-test-only is used")
        source = check_repository(args.repo_root.resolve())
    print(
        f"PASS: cleanup readiness verifier: {model + source:,} checks "
        f"({model:,} model, {source:,} source) across {args.cases:,} randomized cases."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
