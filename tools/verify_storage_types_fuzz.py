#!/usr/bin/env python3
"""Randomized zero-Actions parity for storage file-type/category analytics.

The randomized SQLite function is derived from the committed C# category switch and
its enum ordering, so SQL/reference agreement cannot hide classifier drift.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path
import random
import re

import verify_storage_types as base

ROOT = r"C:\Data"
SIBLING = r"C:\Database"
EXTENSIONS = ("", "txt", "jpg", "png", "bin", "ts", "m2ts", "zip", "db", "iso", "ttf", "exe", "mp3")


def _load_classifier(repo_root: Path):
    contract = (repo_root / "src/FileOp.Core/Storage/IStorageAnalytics.cs").read_text(encoding="utf-8")
    classifier = (repo_root / "src/FileOp.Core/Storage/StorageFileCategoryClassifier.cs").read_text(encoding="utf-8")

    enum_match = re.search(r"public enum StorageFileCategory\s*\{(?P<body>.*?)\}", contract, re.S)
    assert enum_match, "StorageFileCategory enum could not be parsed"
    enum_names = [
        line.strip().rstrip(",")
        for line in enum_match.group("body").splitlines()
        if line.strip() and not line.strip().startswith("//")
    ]
    enum_values = {name: index for index, name in enumerate(enum_names)}
    assert "NoExtension" in enum_values and "Other" in enum_values

    switch_match = re.search(
        r"return normalized switch\s*\{(?P<body>.*?)_ => StorageFileCategory\.Other",
        classifier,
        re.S,
    )
    assert switch_match, "StorageFileCategoryClassifier switch could not be parsed"
    switch_body = switch_match.group("body")

    extension_map: dict[str, int] = {}
    arm_pattern = re.compile(
        r"(?P<values>(?:\s*\"[^\"]+\"\s*(?:or\s*)?)+)"
        r"=>\s*StorageFileCategory\.(?P<category>\w+)\s*,",
        re.S,
    )
    for arm in arm_pattern.finditer(switch_body):
        category_name = arm.group("category")
        assert category_name in enum_values, category_name
        for extension in re.findall(r'\"([^\"]+)\"', arm.group("values")):
            assert extension not in extension_map, f"Duplicate classifier extension: {extension}"
            extension_map[extension] = enum_values[category_name]

    assert len(extension_map) >= 100, f"Classifier parser found only {len(extension_map)} extensions"

    def classify(extension: str) -> int:
        normalized = (extension or "").strip().lstrip(".").lower()
        if not normalized:
            return enum_values["NoExtension"]
        return extension_map.get(normalized, enum_values["Other"])

    return classify, enum_values, extension_map


def _reference(records: list[dict[str, object]], root: str, limit: int, classify) -> list[tuple]:
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
            classify(extension),
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
    for _, category, logical, allocated, unknown, file_count, aliases in ranked_types:
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


def check_randomized(cases: int, repo_root: Path) -> tuple[int, int]:
    classify, enum_values, extension_map = _load_classifier(repo_root)

    # Explicitly prove the randomized set spans every major classifier family.
    assert classify("ts") == enum_values["Code"]
    assert classify("m2ts") == enum_values["Video"]
    assert classify("zip") == enum_values["Archives"]
    assert classify("exe") == enum_values["Applications"]
    assert classify("mp3") == enum_values["Audio"]
    assert classify("iso") == enum_values["DiskImages"]
    assert classify("ttf") == enum_values["Fonts"]
    assert classify("jpg") == enum_values["Images"]
    assert classify("txt") == enum_values["Documents"]
    assert classify("db") == enum_values["Data"]
    assert classify("") == enum_values["NoExtension"]

    for seed in range(cases):
        records = _fixture(seed)
        limit = random.Random(seed ^ 0x5A17).randint(1, 8)
        connection = base._db()
        connection.create_function("fileop_category", 1, classify, deterministic=True)
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

        expected = _reference(records, ROOT, limit, classify)
        assert _normalized(actual) == _normalized(expected), (
            f"seed {seed} diverged\nactual={actual}\nexpected={expected}"
        )

    return len(extension_map), len(enum_values)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--cases", type=int, default=1000)
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    pattern_count, category_count = check_randomized(args.cases, args.repo_root.resolve())
    print(
        f"PASS storage type/category randomized parity: {args.cases} cases; "
        f"{pattern_count} classifier patterns across {category_count} categories"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
