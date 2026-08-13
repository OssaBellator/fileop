#!/usr/bin/env python3
"""Verify the explicit Files file-delete session boundary without .NET or GitHub Actions."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class SessionState:
    source_ready: bool
    source_selected: int
    other_selected: int
    has_directory: bool
    recovery_before: bool
    preflight_ready: bool
    validation_ready: bool
    confirmed: bool
    recovery_after_confirmation: bool
    begin_history_succeeds: bool


@dataclass(frozen=True)
class SessionResult:
    events: tuple[str, ...]
    authorization_issued: bool
    history_begun: bool
    orchestrator_called: bool


def execute(state: SessionState) -> SessionResult:
    events: list[str] = []

    if (
        not state.source_ready
        or state.source_selected <= 0
        or state.other_selected != 0
        or state.has_directory
    ):
        events.append("capture-blocked")
        return SessionResult(tuple(events), False, False, False)

    events.append("recovery-check-before")
    if state.recovery_before:
        events.append("recovery-blocked-before")
        return SessionResult(tuple(events), False, False, False)

    events.append("preflight")
    if not state.preflight_ready:
        events.append("preflight-blocked")
        return SessionResult(tuple(events), False, False, False)

    events.append("execution-validation")
    if not state.validation_ready:
        events.append("validation-blocked")
        return SessionResult(tuple(events), False, False, False)

    events.append("confirmation")
    if not state.confirmed:
        events.append("confirmation-cancelled")
        return SessionResult(tuple(events), False, False, False)

    events.append("recovery-check-after-confirmation")
    if state.recovery_after_confirmation:
        events.append("recovery-blocked-after-confirmation")
        return SessionResult(tuple(events), False, False, False)

    events.append("issue-authorization")
    if not state.begin_history_succeeds:
        events.append("history-begin-failed")
        return SessionResult(tuple(events), True, False, False)

    events.append("begin-history")
    events.append("orchestrate")
    return SessionResult(tuple(events), True, True, True)


def run_model(cases: int, seed: int) -> int:
    checks = 0

    happy = execute(
        SessionState(
            source_ready=True,
            source_selected=2,
            other_selected=0,
            has_directory=False,
            recovery_before=False,
            preflight_ready=True,
            validation_ready=True,
            confirmed=True,
            recovery_after_confirmation=False,
            begin_history_succeeds=True,
        )
    )
    assert happy.authorization_issued
    assert happy.history_begun
    assert happy.orchestrator_called
    assert happy.events == (
        "recovery-check-before",
        "preflight",
        "execution-validation",
        "confirmation",
        "recovery-check-after-confirmation",
        "issue-authorization",
        "begin-history",
        "orchestrate",
    )
    checks += 4

    rng = random.Random(seed)
    for _ in range(cases):
        state = SessionState(
            source_ready=rng.random() < 0.9,
            source_selected=rng.randrange(0, 6),
            other_selected=rng.randrange(0, 3),
            has_directory=rng.random() < 0.2,
            recovery_before=rng.random() < 0.08,
            preflight_ready=rng.random() < 0.9,
            validation_ready=rng.random() < 0.9,
            confirmed=rng.random() < 0.75,
            recovery_after_confirmation=rng.random() < 0.03,
            begin_history_succeeds=rng.random() < 0.98,
        )
        result = execute(state)

        if result.orchestrator_called:
            assert result.authorization_issued
            assert result.history_begun
            assert result.events.index("issue-authorization") < result.events.index("begin-history")
            assert result.events.index("begin-history") < result.events.index("orchestrate")
            checks += 4

        if result.authorization_issued:
            assert state.source_ready
            assert state.source_selected > 0
            assert state.other_selected == 0
            assert not state.has_directory
            assert not state.recovery_before
            assert state.preflight_ready
            assert state.validation_ready
            assert state.confirmed
            assert not state.recovery_after_confirmation
            checks += 9

        if not state.confirmed:
            assert not result.authorization_issued
            assert not result.history_begun
            assert not result.orchestrator_called
            checks += 3

        if state.has_directory or state.other_selected != 0 or state.source_selected <= 0:
            assert not result.authorization_issued
            assert not result.orchestrator_called
            checks += 2

        if state.recovery_before:
            assert not result.authorization_issued
            assert not result.orchestrator_called
            checks += 2

        if state.recovery_after_confirmation and "confirmation" in result.events:
            assert not result.authorization_issued
            assert not result.orchestrator_called
            checks += 2

        if result.history_begun:
            assert result.authorization_issued
            checks += 1

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    files_delete_path = root / "src/FileOp.App/FilesView.Delete.cs"
    files_delete = files_delete_path.read_text(encoding="utf-8")
    files_xaml = (root / "src/FileOp.App/FilesView.xaml").read_text(encoding="utf-8")
    files_refresh = (root / "src/FileOp.App/MainWindow.FilesDelete.cs").read_text(encoding="utf-8")
    review_handoff = (root / "src/FileOp.App/MainWindow.FilesReviewHandoff.cs").read_text(encoding="utf-8")
    cleanup_readiness = (root / "src/FileOp.App/MainWindow.StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    cleanup_view = (root / "src/FileOp.App/StorageKnownLocationReviewView.CleanupReadiness.cs").read_text(encoding="utf-8")
    generic_plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")

    # Catch malformed XAML before the WinUI build is available.
    ET.fromstring(files_xaml)
    checks = 1

    required = (
        (files_xaml, 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"', "WinUI XAML namespace"),
        (files_xaml, 'x:Name="ReviewDeleteLeftButton"', "left delete review action"),
        (files_xaml, 'x:Name="ReviewDeleteRightButton"', "right delete review action"),
        (files_xaml, 'x:Name="RetryDeleteCleanupButton"', "cleanup-only release retry action"),
        (files_xaml, "Permanent file deletion", "destructive scope label"),
        (files_delete, "HasExclusiveFileSelection", "exclusive pane selection guard"),
        (files_delete, "selectedRows.Any(static row => row.IsDirectory)", "directory rejection"),
        (files_delete, "WindowsFileDeleteOperationPreflightValidator", "read-only delete preflight"),
        (files_delete, "WindowsFileDeleteOperationExecutionValidator", "canonical execution validation"),
        (files_delete, "ShowDeleteConfirmationAsync(validation)", "explicit confirmation boundary"),
        (files_delete, "IssueAfterExplicitUserConfirmation(validation)", "authorization issuer"),
        (files_delete, "SqliteFileDeleteOperationRecoveryHistoryReader", "restart recovery discovery"),
        (files_delete, "Environment.SpecialFolder.LocalApplicationData", "persistent per-user history root"),
        (files_delete, '"delete-action-history.sqlite"', "persistent delete history database"),
        (files_delete, "SqliteFileDeleteOperationActionHistoryStore", "durable delete action history"),
        (files_delete, "historyStore.BeginAsync(authorization, CancellationToken.None)", "durable history begin"),
        (files_delete, "FileDeleteOperationOrchestrator.ExecuteAsync", "reviewed delete orchestrator"),
        (files_delete, "WindowsFileDeleteOperationStabilityLeaseProvider", "reviewed stability provider"),
        (files_delete, "WindowsFileDeleteOperationFinalMutationLeaseProvider", "reviewed final capability provider"),
        (files_delete, "FileDeleteOperationFinalLeaseReleaseException? _pendingDeleteLeaseRelease", "retained cleanup owner"),
        (files_delete, "RetryFinalLeaseReleaseAsync", "cleanup-only release retry"),
        (files_delete, "FileDeleteOperationOrchestrationRecoveryRequiredException", "explicit recovery stop"),
        (files_delete, "does not use the Recycle Bin", "permanent-delete confirmation"),
        (files_delete, "did not auto-replay mutation", "no automatic replay status"),
        (files_refresh, "ResetFilesTabPaging", "affected Files cache invalidation"),
        (files_refresh, "LoadFilesDirectoryAsync", "active directory refresh"),
        (review_handoff, "does not authorize deletion", "known-location handoff stays non-authorizing"),
        (cleanup_view, "This is read-only and cannot authorize deletion", "cleanup readiness stays non-authorizing"),
        (gate, "verify_files_delete_session.py --repo-root $repoRoot --cases 50000", "offline delete-session gate wiring"),
    )
    for text, needle, label in required:
        checks += require(text, needle, label)

    for needle, label in (
        ("File.Delete(", "managed path delete"),
        ("Directory.Delete(", "directory/path delete"),
        ("DeleteFileW", "Win32 path delete"),
        ("SafeFileHandle", "raw delete handle"),
        ("FileOperationKind.Delete", "generic delete operation kind"),
    ):
        checks += forbid(files_delete, needle, label)

    for text, label in (
        (review_handoff, "known-location Files handoff"),
        (cleanup_readiness, "Storage cleanup readiness"),
        (cleanup_view, "Storage cleanup readiness view"),
    ):
        checks += forbid(text, "FileDeleteOperationOrchestrator", f"{label} direct mutation wiring")
        checks += forbid(text, "IssueAfterExplicitUserConfirmation", f"{label} delete authorization wiring")

    checks += forbid(generic_plan, "Delete,", "generic FileOperationKind.Delete enum member")

    # The app must have one destructive consumer, and it must be the explicit Files session.
    consumers: list[str] = []
    app_root = root / "src/FileOp.App"
    for path in app_root.rglob("*.cs"):
        if "FileDeleteOperationOrchestrator" in path.read_text(encoding="utf-8"):
            consumers.append(path.relative_to(root).as_posix())
    expected_consumer = files_delete_path.relative_to(root).as_posix()
    if consumers != [expected_consumer]:
        raise AssertionError(
            "reviewed delete orchestrator must have exactly one App consumer: "
            f"expected {[expected_consumer]}, observed {consumers}"
        )
    checks += 1

    # Pin the security-sensitive ordering in source, including the second recovery scan.
    first_recovery = files_delete.index("var recovery = await GetRecoveryCandidateAsync(historyPath)")
    preflight = files_delete.index("_deletePreflightValidator")
    validation = files_delete.index("_deleteExecutionValidator", preflight + 1)
    confirmation = files_delete.index("ShowDeleteConfirmationAsync(validation)")
    second_recovery = files_delete.index("recovery = await GetRecoveryCandidateAsync(historyPath)", confirmation)
    authorization = files_delete.index("IssueAfterExplicitUserConfirmation(validation)")
    history_store = files_delete.index("new SqliteFileDeleteOperationActionHistoryStore(historyPath)")
    begin = files_delete.index("historyStore.BeginAsync(authorization, CancellationToken.None)")
    orchestrate = files_delete.index("FileDeleteOperationOrchestrator.ExecuteAsync")
    if not (
        first_recovery
        < preflight
        < validation
        < confirmation
        < second_recovery
        < authorization
        < history_store
        < begin
        < orchestrate
    ):
        raise AssertionError("Files delete session security-sensitive operation order changed")
    checks += 1

    if files_delete.count("IssueAfterExplicitUserConfirmation(validation)") != 1:
        raise AssertionError("Files delete session must have exactly one authorization issuance site")
    checks += 1
    if files_delete.count("FileDeleteOperationOrchestrator.ExecuteAsync") != 1:
        raise AssertionError("Files delete session must have exactly one orchestrator invocation site")
    checks += 1
    if gate.count("verify_files_delete_session.py") != 1:
        raise AssertionError("Files delete session verifier must be wired exactly once")
    checks += 1

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xF11E5D3)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source/XAML checks" if args.repo_root else ""
    print(
        f"PASS: Files delete session verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
