#!/usr/bin/env python3
"""Verify FileOp's disk-specific I/O attribution contract without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

LONG_MAX = (1 << 63) - 1


def sat_add(left: int, right: int) -> int:
    return LONG_MAX if left > LONG_MAX - right else left + right


@dataclass(frozen=True)
class Owner:
    pid: int
    started_ticks: int | None
    image: str | None


@dataclass(frozen=True)
class Event:
    disk: int
    operation: str
    transfer_bytes: int
    owner: Owner | None


@dataclass
class Transfer:
    read_bytes: int = 0
    write_bytes: int = 0
    read_ops: int = 0
    write_ops: int = 0
    flush_ops: int = 0

    @property
    def total_bytes(self) -> int:
        return sat_add(self.read_bytes, self.write_bytes)

    @property
    def total_ops(self) -> int:
        return sat_add(sat_add(self.read_ops, self.write_ops), self.flush_ops)

    def add_event(self, operation: str, transfer_bytes: int) -> None:
        if operation == "read":
            self.read_bytes = sat_add(self.read_bytes, transfer_bytes)
            self.read_ops = sat_add(self.read_ops, 1)
        elif operation == "write":
            self.write_bytes = sat_add(self.write_bytes, transfer_bytes)
            self.write_ops = sat_add(self.write_ops, 1)
        elif operation == "flush":
            if transfer_bytes != 0:
                raise ValueError("flush cannot carry transfer bytes")
            self.flush_ops = sat_add(self.flush_ops, 1)
        else:
            raise ValueError(operation)

    def add_transfer(self, other: "Transfer") -> None:
        self.read_bytes = sat_add(self.read_bytes, other.read_bytes)
        self.write_bytes = sat_add(self.write_bytes, other.write_bytes)
        self.read_ops = sat_add(self.read_ops, other.read_ops)
        self.write_ops = sat_add(self.write_ops, other.write_ops)
        self.flush_ops = sat_add(self.flush_ops, other.flush_ops)


@dataclass(frozen=True)
class OwnerRow:
    owner: Owner
    transfer: Transfer
    share_percent: float


@dataclass(frozen=True)
class DiskSummary:
    disk: int
    total: Transfer
    unattributed: Transfer
    hidden_identified: Transfer
    hidden_owner_count: int
    owners: tuple[OwnerRow, ...]

    @property
    def identified_bytes(self) -> int:
        return max(0, self.total.total_bytes - self.unattributed.total_bytes)

    @property
    def coverage_percent(self) -> float | None:
        if self.total.total_bytes <= 0:
            return None
        return min(100.0, max(0.0, self.identified_bytes * 100.0 / self.total.total_bytes))


def summarize(events: list[Event], max_owners: int) -> list[DiskSummary]:
    if not 1 <= max_owners <= 100:
        raise ValueError("owner limit")

    totals: dict[int, Transfer] = defaultdict(Transfer)
    unattributed: dict[int, Transfer] = defaultdict(Transfer)
    owners: dict[int, dict[tuple[int, int | None], tuple[Owner, Transfer]]] = defaultdict(dict)

    for event in events:
        if event.transfer_bytes < 0:
            raise ValueError("negative transfer")
        totals[event.disk].add_event(event.operation, event.transfer_bytes)
        if event.owner is None:
            unattributed[event.disk].add_event(event.operation, event.transfer_bytes)
            continue
        if event.owner.pid <= 0:
            raise ValueError("pid")
        key = (event.owner.pid, event.owner.started_ticks)
        if key not in owners[event.disk]:
            owners[event.disk][key] = (event.owner, Transfer())
        owners[event.disk][key][1].add_event(event.operation, event.transfer_bytes)

    result: list[DiskSummary] = []
    for disk in sorted(totals):
        ordered = sorted(
            owners[disk].values(),
            key=lambda item: (
                -item[1].total_bytes,
                -item[1].total_ops,
                item[0].pid,
                -1 if item[0].started_ticks is None else item[0].started_ticks,
            ),
        )
        visible = ordered[:max_owners]
        hidden = ordered[max_owners:]
        hidden_transfer = Transfer()
        for _, transfer in hidden:
            hidden_transfer.add_transfer(transfer)
        total_bytes = totals[disk].total_bytes
        rows = tuple(
            OwnerRow(
                owner,
                transfer,
                0.0 if total_bytes == 0 else min(100.0, max(0.0, transfer.total_bytes * 100.0 / total_bytes)),
            )
            for owner, transfer in visible
        )
        result.append(
            DiskSummary(
                disk,
                totals[disk],
                unattributed[disk],
                hidden_transfer,
                len(hidden),
                rows,
            )
        )
    return result


def run_model(cases: int) -> int:
    checks = 0
    stable = Owner(11, 100, "writer.exe")
    fixed = summarize(
        [
            Event(0, "read", 1000, stable),
            Event(0, "write", 3000, None),
            Event(0, "flush", 0, stable),
        ],
        12,
    )[0]
    assert fixed.total.total_bytes == 4000
    assert fixed.unattributed.total_bytes == 3000
    assert fixed.identified_bytes == 1000
    assert fixed.coverage_percent == 25.0
    assert fixed.owners[0].transfer.flush_ops == 1
    checks += 5

    reused = summarize(
        [
            Event(2, "read", 4000, Owner(77, 10, "worker.exe")),
            Event(2, "read", 6000, Owner(77, 20, "worker.exe")),
        ],
        12,
    )[0]
    assert len(reused.owners) == 2
    assert {row.owner.started_ticks for row in reused.owners} == {10, 20}
    checks += 2

    hidden = summarize(
        [
            Event(1, "write", 9000, Owner(1, 1, "a.exe")),
            Event(1, "read", 7000, Owner(2, 2, "b.exe")),
            Event(1, "write", 5000, Owner(3, 3, "c.exe")),
        ],
        2,
    )[0]
    assert hidden.hidden_owner_count == 1
    assert hidden.hidden_identified.total_bytes == 5000
    assert hidden.total.total_bytes == 21000
    checks += 3

    rng = random.Random(20260810)
    for _ in range(cases):
        disk_count = rng.randint(1, 4)
        max_owners = rng.randint(1, 12)
        event_count = rng.randint(1, 120)
        events: list[Event] = []
        expected_disk_bytes = [0] * disk_count
        expected_unattributed = [0] * disk_count
        expected_disk_ops = [0] * disk_count

        owner_pool: list[Owner] = []
        for pid in range(1, rng.randint(2, 25)):
            started = None if rng.random() < 0.2 else rng.randint(1, 1_000_000)
            owner_pool.append(Owner(pid, started, f"p{pid}.exe"))

        for _event in range(event_count):
            disk = rng.randrange(disk_count)
            operation = rng.choice(["read", "write", "flush"])
            transfer = 0 if operation == "flush" else rng.randint(0, 8 * 1024 * 1024)
            owner = None if rng.random() < 0.15 else rng.choice(owner_pool)
            events.append(Event(disk, operation, transfer, owner))
            expected_disk_bytes[disk] += transfer
            expected_disk_ops[disk] += 1
            if owner is None:
                expected_unattributed[disk] += transfer

        summaries = summarize(events, max_owners)
        observed_disks = sorted({event.disk for event in events})
        assert [summary.disk for summary in summaries] == observed_disks
        checks += 1
        for summary in summaries:
            disk = summary.disk
            assert summary.total.total_bytes == expected_disk_bytes[disk]
            assert summary.total.total_ops == expected_disk_ops[disk]
            assert summary.unattributed.total_bytes == expected_unattributed[disk]
            assert summary.identified_bytes + summary.unattributed.total_bytes == summary.total.total_bytes
            assert len(summary.owners) <= max_owners
            assert summary.hidden_owner_count >= 0
            visible_bytes = sum(row.transfer.total_bytes for row in summary.owners)
            assert (
                visible_bytes
                + summary.hidden_identified.total_bytes
                + summary.unattributed.total_bytes
                == summary.total.total_bytes
            )
            checks += 7
            if summary.total.total_bytes > 0:
                assert summary.coverage_percent is not None
                assert 0.0 <= summary.coverage_percent <= 100.0
                assert all(0.0 <= row.share_percent <= 100.0 for row in summary.owners)
                checks += 3
            else:
                assert summary.coverage_percent is None
                checks += 1
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "model": root / "src/FileOp.Core/Performance/DiskIoAttribution.cs",
        "tests": root / "tests/FileOp.Windows.Tests/DiskIoAttributionTests.cs",
        "doc": root / "docs/disk-io-attribution.md",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    checks = 0
    for needle in (
        "DiskIoOperationKind",
        "PhysicalDiskNumber",
        "DiskIoProcessIdentity",
        "HasStableInstanceIdentity",
        "DiskIoAttributionAnalyzer",
        "DefaultMaxOwnersPerDisk = 12",
        "MaximumOwnersPerDisk = 100",
        "UnattributedBytes",
        "OtherIdentifiedBytes",
        "AttributionCoveragePercent",
        "owner.AddTo(other)",
        "owner.StartedAt?.ToUniversalTime().UtcDateTime.Ticks",
        "processStart > observation.Timestamp",
        "DiskIoOperationKind.Flush && observation.TransferBytes != 0",
    ):
        assert needle in text["model"], needle
        checks += 1

    for needle in (
        "AggregatesReadWriteFlushAndObservedByteSharePerPhysicalDisk",
        "UnresolvedOwnershipRemainsVisibleAndReducesCoverage",
        "OwnerLimitPreservesHiddenIdentifiedTotals",
        "SamePidDifferentStartTimesRemainSeparateProcessInstances",
        "MissingStartTimeIsExplicitlyWeakIdentityButStillBoundedToCaptureWindow",
        "PhysicalDisksAreNeverMixed",
        "InvalidProviderEvidenceFailsClosed",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "system/kernel DiskIo ETW stream",
        "physical `DiskNumber`",
        "`TransferSize` in bytes",
        "issuing thread identity",
        "Unresolved ownership is first-class evidence",
        "not a confidence score",
        "does not by itself establish a storage bottleneck",
        "does **not** start an ETW session",
    ):
        assert needle in text["doc"], needle
        checks += 1

    for forbidden in (
        "DllImport",
        "StartTrace(",
        "ControlTrace(",
        "ProcessTrace(",
        "GetProcessIoCounters",
        "PerformanceCounter",
        "DispatcherQueueTimer",
        "PeriodicTimer",
        "Registry.",
        "ServiceController",
    ):
        assert forbidden not in text["model"], forbidden
        checks += 1

    assert "verify_disk_io_attribution.py" in text["gate"]
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
        "PASS: disk-specific I/O attribution contract verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
