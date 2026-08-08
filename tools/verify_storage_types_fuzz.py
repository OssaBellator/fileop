#!/usr/bin/env python3
"""Randomized zero-Actions property checks for storage file-type/category analytics."""
from __future__ import annotations

import argparse
from collections import defaultdict
import random

import verify_storage_types as base

ROOT = r"C:\Data"
SIBLING = r"C:\Database"
EXTENSIONS = ("", "txt", "jpg", "png", "bin", "ts", "m2ts", "zip", "db")


def _reference(records: list[dict[str, object]], root: str, limit: int) -> list[tuple]:
    children: dict[str, list[dict[str, object]]] = defaultdict(list)
    for record in records:
        children[str(record["parent"]).lower()].append(record)

    scoped: list[dict[str, object]] = []
    stack = [root.lower()]
    while stack:
        parent = stack.pop()
        for record in children.get(parent, []):
            scoped.append(record)
            if bool(record["directory"]):
                stack.append(str(record["path"]).lower())

    files = [record for record in scoped if not bool(record["directory"])]
    identities: dict[tuple[int, int], list[dict[str, object]]] = defaultdict(list)
    for record in files:
        identity = record["identity"]
        if identity is not None:
            identities[identity].append(record)

    canonical_paths = {
        identity: min(str(record["path"]).lower() for record in group)
        for identity, group in identities.items()
    }
    aggregates: dict[str, dict[str, int]] = defaultdict(
        lambda: {"logical": 0, "allocated": 0, "unknown": 0, "files": 0, "aliases": 0}
    )

    for record in files:
        extension = str(record["extension"])
        aggregate = aggregates[extension]
        aggregate["logical"] += int(record["logical"])
        aggregate["files"] += 1
        identity = record["identity"]
        owns_allocation = identity is None or str(record["path"]).lower() == canonical_paths[identity]
        if not owns_allocation:
            aggregate["aliases"] += 1
            continue

        allocated = record["allocated"]
        if allocated is None:
            aggregate["unknown"] += 1
        else:
            aggregate["allocated"] += int(allocated)

    total_logical = sum(item["logical"] for item in aggregates.values())
    total_allocated = sum(item["allocated"] for item in aggregates.values())
    total_unknown = sum(item["unknown"] for item in aggregates.values())
    total_files = sum(item["files"] for item in aggregates.values())
    total_aliases = sum(item["aliases"] for item in aggregates.values())
    type_count = len(aggregates)
    totals = (total_logical, total_allocated, total_unknown, total_files, total_aliases, type_count)

    ranked_types = [
        (
            extension,
            base._category(extension),
            aggregate["logical"],
            aggregate["allocated"],
            aggregate["unknown"],
            aggregate["files"],
            aggregate["aliases"],
        )
        for extension, aggregate in aggregates.items()
    ]
    ranked_types.sort(
        key=lambda row: (
            -(row[3] if total_unknown == 0 else row[2]),
            -row[2],
            row[0].lower(),
        )
    )

    rows = [
        (0, extension, category, logical, allocated, unknown, file_count, aliases, 1, *totals)
        for extension, category, logical, allocated, unknown, file_count, aliases in ranked_types[:limit]
    ]

    category_aggregates: dict[int, dict[str, int]] = defaultdict(
        lambda: {"logical": 0, "allocated": 0, "unknown": 0, "files": 0, "aliases": 0, "types": 0}
    )
    for extension, category, logical, allocated, unknown, file_count, aliases in ranked_types:
        aggregate = category_aggregates[category]
        aggregate["logical"] += logical
        aggregate["allocated"] += allocated
        aggregate["unknown"] += unknown
        aggregate["files"] += file_count
        aggregate["aliases"] += aliases
        aggregate["types"] += 1

    rows.extend(
        (
            1,
            "",
            category,
            aggregate["logical"],
            aggregate["allocated"],
            aggregate["unknown"],
            aggregate["files"],
            aggregate["aliases"],
            aggregate["types"],
            *totals,
        )
        for category, aggregate in category_aggregates.items()
    )
    return rows


def _fixture(seed: int) -> list[dict[str, object]]:
    rng = random.Random(seed)
    records: list[dict[str, object]] = []
    directories = [ROOT]

    for ordinal in range(rng.randint(1, 8)):
        parent = rng.choice(directories)
        path = parent + f"\\D{ordinal}"
        records.append(
            {
                "path": path,
                "parent": parent,
                "extension": "",
                "logical": 0,
                "allocated": 0,
                "directory": True,
                "identity": None,
            }
        )
        directories.append(path)

    identities = [(0xCAFE, ordinal) for ordinal in range(1, rng.randint(2, 8))]
    for ordinal in range(rng.randint(1, 60)):
        parent = SIBLING if rng.random() < 0.1 else rng.choice(directories)
        extension = rng.choice(EXTENSIONS)
        suffix = f".{extension}" if extension else ""
        identity = rng.choice(identities) if identities and rng.random() < 0.35 else None
        records.append(
            {
                "path": parent + f"\\file-{ordinal}{suffix}",
                "parent": parent,
                "extension": extension,
                "logical": rng.randint(0, 1000),
                "allocated": None if rng.random() < 0.2 else rng.randint(0, 1200),
                "directory": False,
                "identity": identity,
            }
        )

    by_identity: dict[tuple[int, int], list[dict[str, object]]] = defaultdict(list)
    for record in records:
        identity = record["identity"]
        if identity is not None:
            by_identity[identity].append(record)
    for group in by_identity.values():
        logical = group[0]["logical"]
        allocated = group[0]["allocated"]
        for record in group[1:]:
            record["logical"] = logical
            record["allocated"] = allocated

    return records


def _normalized(rows: list[tuple]) -> list[tuple]:
    return sorted(rows, key=lambda row: (row[0], str(row[1]).lower(), row[2]))


def check_randomized(cases: int) -> None:
    for seed in range(cases):
        records = _fixture(seed)
        limit = random.Random(seed ^ 0x5A17).randint(1, 8)
        connection = base._db()
        try:
            for record in records:
                base._add(
                    connection,
                    str(record["path"]),
                    str(record["parent"]),
                    str(record["extension"]),
                    int(record["logical"]),
                    record["allocated"],
                    bool(record["directory"]),
                    record["identity"],
                )
            actual = base._query(connection, ROOT, limit)
        finally:
            connection.close()

        expected = _reference(records, ROOT, limit)
        assert _normalized(actual) == _normalized(expected), (
            f"seed {seed} diverged\nactual={actual}\nexpected={expected}"
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=int, default=1000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    check_randomized(args.cases)
    print(f"PASS storage file-type/category randomized parity: {args.cases} cases")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
