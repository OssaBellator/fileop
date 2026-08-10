#!/usr/bin/env python3
"""Verify FileOp's read-only storage-optimization semantics without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Record:
    path: str
    logical: int
    allocated: int | None
    age_days: int
    identity: tuple[int, int] | None

    @property
    def measured(self) -> int:
        return self.logical if self.allocated is None else self.allocated


def canonicalize(records: list[Record]) -> list[Record]:
    by_identity: dict[tuple[object, ...], list[Record]] = {}
    for record in records:
        key: tuple[object, ...]
        if record.identity is None:
            key = ("path", record.path.casefold())
        else:
            key = ("identity", *record.identity)
        by_identity.setdefault(key, []).append(record)
    return [
        min(group, key=lambda record: record.path.casefold())
        for group in by_identity.values()
    ]


def analyze(
    records: list[Record],
    *,
    large_minimum: int,
    same_size_minimum: int,
    stale_age_days: int,
) -> tuple[list[Record], list[Record], list[tuple[int, list[Record], int]]]:
    physical = canonicalize(records)
    largest = sorted(
        (record for record in physical if record.measured >= large_minimum),
        key=lambda record: (-record.measured, -record.logical, record.path.casefold()),
    )
    stale = [record for record in largest if record.age_days > stale_age_days]

    size_groups: dict[int, list[Record]] = {}
    for record in physical:
        if record.logical >= same_size_minimum:
            size_groups.setdefault(record.logical, []).append(record)
    groups = []
    for logical, members in size_groups.items():
        if len(members) < 2:
            continue
        upper = logical * (len(members) - 1)
        groups.append((logical, sorted(members, key=lambda record: record.path.casefold()), upper))
    groups.sort(key=lambda group: (-group[2], -group[0]))
    return largest, stale, groups


def run_model(cases: int) -> int:
    checks = 0
    fixed = [
        Record(r"C:\scope\recent.iso", 2_000, 1_500, 10, (1, 10)),
        Record(r"C:\scope\old.bin", 1_200, 1_100, 300, (1, 11)),
        Record(r"C:\scope\same-a.zip", 800, 800, 20, (1, 12)),
        Record(r"C:\scope\same-a-alias.zip", 800, 800, 20, (1, 12)),
        Record(r"C:\scope\same-b.zip", 800, 800, 30, (1, 13)),
    ]
    largest, stale, groups = analyze(
        fixed,
        large_minimum=1_000,
        same_size_minimum=500,
        stale_age_days=180,
    )
    assert [record.path for record in largest] == [
        r"C:\scope\recent.iso",
        r"C:\scope\old.bin",
    ]
    assert [record.path for record in stale] == [r"C:\scope\old.bin"]
    assert len(groups) == 1
    assert groups[0][0] == 800 and len(groups[0][1]) == 2 and groups[0][2] == 800
    assert sum("same-a" in record.path for record in groups[0][1]) == 1
    checks += 5

    rng = random.Random(20260810)
    for case in range(cases):
        records: list[Record] = []
        expected_physical: set[tuple[object, ...]] = set()
        for index in range(rng.randint(2, 80)):
            logical = rng.choice([64, 128, 256, 512, 1024, 2048]) * 1024 * 1024
            allocated = None if rng.randrange(5) == 0 else rng.randint(1, max(1, logical // 4096)) * 4096
            identity = (1, rng.randint(1, max(2, index + 1))) if rng.randrange(5) else None
            path = rf"C:\scope\case-{case}\file-{index}.bin"
            records.append(Record(path, logical, allocated, rng.randint(0, 720), identity))
            expected_physical.add(("path", path.casefold()) if identity is None else ("identity", *identity))

        physical = canonicalize(records)
        assert len(physical) == len(expected_physical)
        checks += 1

        largest, stale, groups = analyze(
            records,
            large_minimum=512 * 1024 * 1024,
            same_size_minimum=128 * 1024 * 1024,
            stale_age_days=180,
        )
        assert all(record.measured >= 512 * 1024 * 1024 for record in largest)
        assert all(record.age_days > 180 for record in stale)
        assert all(record in largest for record in stale)
        checks += 3

        seen_ids: set[tuple[int, int]] = set()
        for _, members, upper in groups:
            assert len(members) >= 2
            assert len({member.logical for member in members}) == 1
            assert upper == members[0].logical * (len(members) - 1)
            for member in members:
                if member.identity is not None:
                    assert member.identity not in seen_ids
                    seen_ids.add(member.identity)
            checks += 3
        seen_ids.clear()

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "model": root / "src/FileOp.Core/Storage/StorageOptimization.cs",
        "sqlite": root / "src/FileOp.Core/Storage/SqliteStorageOptimizationAnalytics.cs",
        "protocol": root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs",
        "backend": root / "src/FileOp.Windows/IndexingService/StorageOptimizationIndexingServiceBackend.cs",
        "client": root / "src/FileOp.Windows/IndexingService/IndexingServiceClient.cs",
        "desktop": root / "src/FileOp.App/DesktopSearchEngine.StorageOptimization.cs",
        "program": root / "src/FileOp.Indexer/Program.cs",
        "gate": root / "tools/test-local.ps1",
    }
    text = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    checks = 0
    for needle in (
        "public const int CurrentVersion = 7;",
        "AnalyzeStorageOptimization",
        "IndexingStorageOptimizationRequest",
        "IndexingStorageOptimizationResponse",
    ):
        assert needle in text["protocol"], needle
        checks += 1

    for needle in (
        "physical_rank = 1",
        "volume_serial",
        "file_reference",
        "HAVING COUNT(*) >= 2",
        "SameSizeMinimumBytes",
        "last_write_utc_ticks < @stale_before_ticks",
    ):
        assert needle in text["sqlite"], needle
        checks += 1

    for needle in (
        "PotentialLogicalSavingsUpperBound",
        "SameSizeCandidateGroups",
        "AllocatedBytes ?? LogicalBytes",
    ):
        assert needle in text["model"], needle
        checks += 1

    assert "StorageOptimizationIndexingServiceBackend" in text["program"]
    assert "TryAcquireRead" in text["backend"]
    assert "HasCheckpointAsync" in text["backend"]
    assert "AnalyzeStorageOptimizationAsync" in text["client"]
    assert "StorageOptimizationAvailable" in text["desktop"]
    checks += 5

    forbidden = ("File.Delete(", "Directory.Delete(", "DELETE FROM", "SHA256", "Registry.")
    optimization_source = "\n".join(
        text[name] for name in ("model", "sqlite", "backend", "desktop")
    )
    for needle in forbidden:
        assert needle not in optimization_source, needle
        checks += 1

    assert "verify_storage_optimization.py" in text["gate"]
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=10_000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repository_checks = 0
    if args.repo_root is not None:
        repository_checks = check_repository(args.repo_root.resolve())
    suffix = f" and {repository_checks:,} source/wiring checks" if args.repo_root else ""
    print(
        "PASS: storage optimization semantics verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
