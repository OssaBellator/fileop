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
    if current_reparse:
        return BLOCKED
    if not same_current_path_identity or not size_match or not time_match:
        return CHANGED
    return CONSISTENT


def run_model(cases: int, seed: int) -> int:
    checks = 0
    assert analyze(
        root_state="directory", root_reparse=False,
        candidate_state="file", candidate_reparse=False,
        same_volume=True, within_root=True, current_present=True,
        current_reparse=False, same_current_path_identity=True,
        size_match=True, time_match=True,
    ) == CONSISTENT
    assert analyze(
        root_state="directory", root_reparse=False,
        candidate_state="file", candidate_reparse=False,
        same_volume=True, within_root=False, current_present=True,
        current_reparse=False, same_current_path_identity=True,
        size_match=True, time_match=True,
    ) == BLOCKED
    assert analyze(
        root_state="directory", root_reparse=False,
        candidate_state="missing", candidate_reparse=False,
        same_volume=True, within_root=True, current_present=False,
        current_reparse=False, same_current_path_identity=False,
        size_match=False, time_match=False,
    ) == CHANGED
    assert analyze(
        root_state="directory", root_reparse=False,
        candidate_state="file", candidate_reparse=False,
        same_volume=True, within_root=True, current_present=True,
        current_reparse=False, same_current_path_identity=False,
        size_match=True, time_match=True,
    ) == CHANGED
    checks += 4

    rng = random.Random(seed)
    root_states = ("directory", "missing", "file", "inaccessible", "error")
    candidate_states = ("file", "missing", "directory", "inaccessible", "error")
    for _ in range(cases):
        values = dict(
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
        assert (result == CONSISTENT) == (
            values["root_state"] == "directory"
            and not values["root_reparse"]
            and values["candidate_state"] == "file"
            and not values["candidate_reparse"]
            and values["same_volume"]
            and values["within_root"]
            and values["current_present"]
            and not values["current_reparse"]
            and values["same_current_path_identity"]
            and values["size_match"]
            and values["time_match"]
        )
        if values["root_state"] in {"inaccessible", "error"}:
            assert result == UNAVAILABLE
        if values["root_state"] == "directory" and values["root_reparse"]:
            assert result == BLOCKED
        if (
            values["root_state"] == "directory"
            and not values["root_reparse"]
            and values["candidate_state"] == "file"
            and values["candidate_reparse"]
        ):
            assert result == BLOCKED
        if result == CONSISTENT:
            assert values["same_current_path_identity"] and values["size_match"] and values["time_match"]
        checks += 6
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
        "canonicalReviewRoot.IsLeafReparsePoint",
        "canonicalCandidate.IsLeafReparsePoint",
        "canonicalReviewRoot.Identity.Value.VolumeSerialNumber",
        "canonicalCandidate.Identity.Value.VolumeSerialNumber",
        "!IsPathWithinRoot(canonicalCandidate.CanonicalPath, canonicalReviewRoot.CanonicalPath)",
        "currentFile.Identity != canonicalCandidate.Identity.Value",
        "currentFile.LogicalBytes != candidate.LogicalBytes",
        "currentFile.LastWriteTimeUtc.UtcDateTime.Ticks !=",
        "candidate.LastWriteTime.ToUniversalTime().UtcDateTime.Ticks",
        "CleanupMutationAuthorized => false",
        "continuity of the same file object since indexing is not proven",
        "Deletion is still unavailable",
    ):
        checks += require(core, needle)

    for needle in (
        "WindowsFileOperationCanonicalPathResolver",
        "dwDesiredAccess: 0",
        "FileShare.ReadWrite | FileShare.Delete",
        "GetFinalPathNameByHandleW",
        "GetFileInformationByHandle",
        "new FileIdentity(",
        "DateTime.FromFileTimeUtc",
        "StorageCleanupReadinessAnalyzer.Analyze",
    ):
        checks += require(windows, needle)
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
        "Delete recovery/history and mutation authorization are not implemented",
    ):
        checks += require(xaml, needle)
    for needle in (
        "CheckKnownLocationCleanupReadinessAsync(row.Path)",
        "ApplyCleanupReadiness",
        "CleanupMutationAuthorized",
    ):
        if needle == "CleanupMutationAuthorized":
            checks += forbid(view, needle)
        else:
            checks += require(view, needle)

    for needle in (
        "No previous readiness result is retained as current evidence",
        "Cleanup readiness has not been checked for this review",
    ):
        checks += require(lifecycle, needle)

    for needle in (
        "!await _storageGate.WaitAsync(0)",
        "_performanceDiskIoCaptureActive || _storageSameSizeVerificationActive",
        "PathsEqual(candidate.Path, requestedPath)",
        "matchedLocation.Status != StorageReviewLocationStatus.Available",
        "IsPathWithinRoot(matchedCandidate.Path, matchedLocation.RootPath)",
        "_storageCleanupReadinessService.PreviewAsync",
        "ReferenceEquals(review, _storageKnownLocationReview)",
        "ApplyKnownLocationCleanupReadiness(preview)",
        "_storageGate.Release();",
    ):
        checks += require(coordinator, needle)

    for needle in (
        "MatchingCurrentEvidenceRemainsNonAuthorizing",
        "IdentityChangeBetweenCurrentReadsFailsClosed",
        "CanonicalEscapeIsBlocked",
        "CandidateReparsePointIsBlocked",
        "CrossVolumeResolutionIsBlocked",
        "CurrentMetadataFailureDoesNotBecomeReady",
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
        "does not yet provide a reviewed delete executor",
        "does not prove that this is the same physical file object that was indexed earlier",
        "CleanupMutationAuthorized` is always `false`",
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
