#!/usr/bin/env python3
"""Verify FileOp's bounded disk-I/O capture policy without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path

MIN_DURATION_MS = 250
MAX_DURATION_MS = 5_000
DEFAULT_DURATION_MS = 2_000
DEFAULT_MAX_OBSERVATIONS = 100_000
MAX_OBSERVATIONS = 500_000
DEFAULT_MAX_OWNERS = 12
MAX_OWNERS = 100


@dataclass(frozen=True)
class Budget:
    duration_ms: int
    max_observations: int
    max_owners: int

    def __post_init__(self) -> None:
        if not MIN_DURATION_MS <= self.duration_ms <= MAX_DURATION_MS:
            raise ValueError("duration")
        if not 1 <= self.max_observations <= MAX_OBSERVATIONS:
            raise ValueError("observations")
        if not 1 <= self.max_owners <= MAX_OWNERS:
            raise ValueError("owners")


@dataclass(frozen=True)
class Report:
    duration_ms: int
    accepted_events: int
    max_owners: int


@dataclass(frozen=True)
class Result:
    status: str
    report: Report | None
    stop_reason: str | None
    loss_state: str
    lost_count: int | None
    overhead_ms: int | None

    @property
    def incomplete(self) -> bool:
        return self.status == "completed" and (
            self.stop_reason == "limit" or self.loss_state != "none"
        )


def validate_result(budget: Budget, result: Result) -> None:
    if result.status not in {"completed", "unsupported", "permission", "session"}:
        raise ValueError("status")
    if result.loss_state not in {"none", "observed", "unknown"}:
        raise ValueError("loss")
    if result.stop_reason not in {None, "duration", "limit"}:
        raise ValueError("stop")
    if result.overhead_ms is not None and result.overhead_ms < 0:
        raise ValueError("overhead")
    if result.lost_count is not None and result.lost_count < 0:
        raise ValueError("lost")

    if result.status != "completed":
        if result.report is not None or result.stop_reason is not None:
            raise ValueError("unavailable payload")
        if result.loss_state != "unknown" or result.lost_count is not None:
            raise ValueError("unavailable loss")
        return

    if result.report is None or result.stop_reason is None:
        raise ValueError("completed payload")
    if result.report.duration_ms < 0:
        raise ValueError("negative report duration")
    if result.report.duration_ms > budget.duration_ms:
        raise ValueError("duration mismatch")
    if result.report.accepted_events < 0:
        raise ValueError("negative report events")
    if result.report.accepted_events > budget.max_observations:
        raise ValueError("event mismatch")
    if result.report.max_owners != budget.max_owners:
        raise ValueError("owner mismatch")
    if result.stop_reason == "duration" and result.report.duration_ms != budget.duration_ms:
        raise ValueError("full-duration mismatch")
    if result.stop_reason == "limit" and result.report.accepted_events != budget.max_observations:
        raise ValueError("limit mismatch")

    if result.loss_state == "none" and result.lost_count != 0:
        raise ValueError("none count")
    if result.loss_state == "observed" and (result.lost_count is None or result.lost_count <= 0):
        raise ValueError("observed count")
    if result.loss_state == "unknown" and result.lost_count is not None:
        raise ValueError("unknown count")


def run_model(cases: int) -> int:
    checks = 0
    default = Budget(DEFAULT_DURATION_MS, DEFAULT_MAX_OBSERVATIONS, DEFAULT_MAX_OWNERS)
    assert default.duration_ms == 2_000
    assert default.max_observations == 100_000
    assert default.max_owners == 12
    checks += 3

    for invalid in (
        lambda: Budget(249, 1, 1),
        lambda: Budget(5_001, 1, 1),
        lambda: Budget(250, 0, 1),
        lambda: Budget(250, 500_001, 1),
        lambda: Budget(250, 1, 0),
        lambda: Budget(250, 1, 101),
    ):
        try:
            invalid()
            raise AssertionError("invalid budget accepted")
        except ValueError:
            checks += 1

    rng = random.Random(20260810)
    for _ in range(cases):
        budget = Budget(
            rng.randint(MIN_DURATION_MS, MAX_DURATION_MS),
            rng.randint(1, MAX_OBSERVATIONS),
            rng.randint(1, MAX_OWNERS),
        )
        accepted = rng.randint(0, budget.max_observations)
        stop = rng.choice(["duration", "limit"])
        if stop == "limit":
            accepted = budget.max_observations
            report_duration = rng.randint(0, budget.duration_ms)
        else:
            report_duration = budget.duration_ms
        loss = rng.choice(["none", "observed", "unknown"])
        lost = 0 if loss == "none" else rng.randint(1, 10_000) if loss == "observed" else None
        result = Result(
            "completed",
            Report(report_duration, accepted, budget.max_owners),
            stop,
            loss,
            lost,
            rng.choice([None, rng.randint(0, 2_000)]),
        )
        validate_result(budget, result)
        assert result.incomplete == (stop == "limit" or loss != "none")
        assert result.report is not None and 0 <= result.report.duration_ms <= budget.duration_ms
        assert 0 <= result.report.accepted_events <= budget.max_observations
        assert result.report.max_owners == budget.max_owners
        if stop == "duration":
            assert result.report.duration_ms == budget.duration_ms
        else:
            assert result.report.accepted_events == budget.max_observations
        checks += 5

        unavailable = Result(
            rng.choice(["unsupported", "permission", "session"]),
            None,
            None,
            "unknown",
            None,
            rng.choice([None, rng.randint(0, 2_000)]),
        )
        validate_result(budget, unavailable)
        assert not unavailable.incomplete
        assert unavailable.report is None and unavailable.stop_reason is None
        checks += 2

        mismatched_owner_count = budget.max_owners - 1 if budget.max_owners > 1 else 2
        invalid_variants = [
            Result("completed", Report(-1, accepted, budget.max_owners), "limit", "none", 0, None),
            Result("completed", Report(budget.duration_ms, -1, budget.max_owners), "duration", "none", 0, None),
            Result("completed", Report(budget.duration_ms + 1, accepted, budget.max_owners), "duration", "none", 0, None),
            Result("completed", Report(max(0, budget.duration_ms - 1), accepted, budget.max_owners), "duration", "none", 0, None),
            Result("completed", Report(budget.duration_ms, budget.max_observations + 1, budget.max_owners), "duration", "none", 0, None),
            Result("completed", Report(budget.duration_ms, accepted, mismatched_owner_count), "duration", "none", 0, None),
            Result("completed", Report(budget.duration_ms, max(0, budget.max_observations - 1), budget.max_owners), "limit", "none", 0, None),
            Result("completed", Report(budget.duration_ms, accepted, budget.max_owners), "duration", "none", None, None),
            Result("completed", Report(budget.duration_ms, accepted, budget.max_owners), "duration", "observed", 0, None),
            Result("completed", Report(budget.duration_ms, accepted, budget.max_owners), "duration", "unknown", 1, None),
            Result("permission", Report(0, 0, budget.max_owners), None, "unknown", None, None),
            Result("session", None, None, "none", 0, None),
            Result("unsupported", None, None, "unknown", None, -1),
        ]
        for invalid in invalid_variants:
            try:
                validate_result(budget, invalid)
                raise AssertionError("invalid result accepted")
            except ValueError:
                checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "contract": root / "src/FileOp.Core/Performance/DiskIoCapture.cs",
        "attribution": root / "src/FileOp.Core/Performance/DiskIoAttribution.cs",
        "tests": root / "tests/FileOp.Windows.Tests/DiskIoCaptureTests.cs",
        "doc": root / "docs/disk-io-capture-policy.md",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    checks = 0
    for needle in (
        "DefaultDuration = TimeSpan.FromSeconds(2)",
        "MinimumDuration = TimeSpan.FromMilliseconds(250)",
        "MaximumDuration = TimeSpan.FromSeconds(5)",
        "DefaultMaxObservations = 100_000",
        "MaximumObservations = 500_000",
        "IDiskIoAttributionProvider",
        "CancellationToken cancellationToken = default",
        "DiskIoCaptureStatus",
        "PermissionRequired",
        "SessionUnavailable",
        "DiskIoCaptureStopReason",
        "ObservationLimitReached",
        "DiskIoCaptureLossState",
        "EvidenceMayBeIncomplete",
        "ProviderOverheadDuration",
        "report.ObservationDuration < TimeSpan.Zero",
        "report.ObservationDuration > budget.Duration",
        "report.AcceptedEventCount < 0",
        "report.AcceptedEventCount > budget.MaxObservations",
        "report.MaxOwnersPerDisk != budget.MaxOwnersPerDisk",
        "report.ObservationDuration != budget.Duration",
    ):
        assert needle in text["contract"], needle
        checks += 1

    assert "MaximumOwnersPerDisk = 100" in text["attribution"]
    checks += 1

    for needle in (
        "DefaultBudgetIsShortAndBounded",
        "InvalidBudgetValuesFailClosed",
        "CompletedNoLossResultIsNotMarkedIncomplete",
        "ObservationLimitAndEventLossAreExplicitlyIncomplete",
        "CompletedResultMustMatchItsBudget",
        "MalformedManualReportsFailClosed",
        "LossStateAndCountMustAgree",
        "UnavailableResultsCannotMasqueradeAsSuccessfulEvidence",
        "ResultRejectsNegativeOverheadAndBlankDetail",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "workload-safety policy, not statistical guarantees",
        "Cancellation is a caller-controlled operation boundary",
        "full requested observation window",
        "PermissionRequired",
        "SessionUnavailable",
        "EvidenceMayBeIncomplete",
        "not a quality/health score",
        "negative duration",
        "does **not** start, control or consume an ETW session",
    ):
        assert needle in text["doc"], needle
        checks += 1

    for forbidden in (
        "DllImport",
        "StartTrace(",
        "ControlTrace(",
        "ProcessTrace(",
        "EnableTraceEx2(",
        "DispatcherQueueTimer",
        "PeriodicTimer",
        "Registry.",
        "ServiceController",
        "runas",
    ):
        assert forbidden not in text["contract"], forbidden
        checks += 1

    assert "verify_disk_io_capture_policy.py" in text["gate"]
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
    repository_checks = 0
    if args.repo_root is not None:
        repository_checks = check_repository(args.repo_root.resolve())
    suffix = f" and {repository_checks:,} source/test/doc checks" if args.repo_root else ""
    print(
        "PASS: bounded disk-I/O capture policy verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
