#!/usr/bin/env python3
"""Offline checks for FileOp storage file-type/category analytics.

No third-party packages, .NET SDK, Windows runtime, or GitHub Actions required.

    python tools/verify_storage_types.py --self-test-only
    python tools/verify_storage_types.py --repo-root .
"""
from __future__ import annotations

import argparse
from collections import Counter
import re
import sqlite3
import sys
import textwrap
from pathlib import Path

# Keep this byte-for-byte equivalent (after raw-string dedent) to
# SqliteStorageAnalytics.FileTypeAnalysisSql. Repository mode enforces that.
FILE_TYPE_SQL = """
WITH RECURSIVE tree(
    path,
    path_norm,
    extension_norm,
    is_directory,
    length,
    allocated_length,
    volume_serial,
    file_reference
) AS (
    SELECT
        path,
        path_norm,
        extension_norm,
        is_directory,
        length,
        allocated_length,
        volume_serial,
        file_reference
    FROM files
    WHERE parent_path = @root COLLATE NOCASE

    UNION ALL

    SELECT
        child.path,
        child.path_norm,
        child.extension_norm,
        child.is_directory,
        child.length,
        child.allocated_length,
        child.volume_serial,
        child.file_reference
    FROM files AS child
    JOIN tree ON child.parent_path = tree.path COLLATE NOCASE
),
physical_rows AS (
    SELECT
        tree.*,
        CASE
            WHEN is_directory = 1 THEN 1
            ELSE ROW_NUMBER() OVER (
                PARTITION BY
                    volume_serial,
                    file_reference,
                    CASE
                        WHEN volume_serial IS NULL OR file_reference IS NULL THEN path_norm
                        ELSE ''
                    END
                ORDER BY path_norm
            )
        END AS physical_rank
    FROM tree
),
aggregates AS (
    SELECT
        extension_norm,
        SUM(length) AS logical_bytes,
        SUM(CASE
            WHEN physical_rank = 1 THEN COALESCE(allocated_length, 0)
            ELSE 0
        END) AS allocated_known_bytes,
        SUM(CASE
            WHEN physical_rank = 1 AND allocated_length IS NULL THEN 1
            ELSE 0
        END) AS unknown_allocated_files,
        COUNT(*) AS file_count,
        SUM(CASE WHEN physical_rank > 1 THEN 1 ELSE 0 END) AS hard_link_alias_count
    FROM physical_rows
    WHERE is_directory = 0
    GROUP BY extension_norm
),
ranked_types AS (
    SELECT
        extension_norm,
        fileop_category(extension_norm) AS category,
        logical_bytes,
        allocated_known_bytes,
        unknown_allocated_files,
        file_count,
        hard_link_alias_count,
        SUM(logical_bytes) OVER () AS total_logical_bytes,
        SUM(allocated_known_bytes) OVER () AS total_allocated_known_bytes,
        SUM(unknown_allocated_files) OVER () AS total_unknown_allocated_files,
        SUM(file_count) OVER () AS total_file_count,
        SUM(hard_link_alias_count) OVER () AS total_hard_link_alias_count,
        COUNT(*) OVER () AS type_count
    FROM aggregates
),
ordered_types AS (
    SELECT
        *,
        ROW_NUMBER() OVER (
            ORDER BY
                CASE
                    WHEN total_unknown_allocated_files = 0 THEN allocated_known_bytes
                    ELSE logical_bytes
                END DESC,
                logical_bytes DESC,
                extension_norm COLLATE NOCASE
        ) AS type_rank
    FROM ranked_types
),
category_aggregates AS (
    SELECT
        category,
        SUM(logical_bytes) AS logical_bytes,
        SUM(allocated_known_bytes) AS allocated_known_bytes,
        SUM(unknown_allocated_files) AS unknown_allocated_files,
        SUM(file_count) AS file_count,
        SUM(hard_link_alias_count) AS hard_link_alias_count,
        COUNT(*) AS category_type_count,
        MAX(total_logical_bytes) AS total_logical_bytes,
        MAX(total_allocated_known_bytes) AS total_allocated_known_bytes,
        MAX(total_unknown_allocated_files) AS total_unknown_allocated_files,
        MAX(total_file_count) AS total_file_count,
        MAX(total_hard_link_alias_count) AS total_hard_link_alias_count,
        MAX(type_count) AS type_count
    FROM ranked_types
    GROUP BY category
)
SELECT
    0 AS row_kind,
    extension_norm,
    category,
    logical_bytes,
    allocated_known_bytes,
    unknown_allocated_files,
    file_count,
    hard_link_alias_count,
    1 AS category_type_count,
    total_logical_bytes,
    total_allocated_known_bytes,
    total_unknown_allocated_files,
    total_file_count,
    total_hard_link_alias_count,
    type_count
FROM ordered_types
WHERE type_rank <= @limit

UNION ALL

SELECT
    1 AS row_kind,
    '' AS extension_norm,
    category,
    logical_bytes,
    allocated_known_bytes,
    unknown_allocated_files,
    file_count,
    hard_link_alias_count,
    category_type_count,
    total_logical_bytes,
    total_allocated_known_bytes,
    total_unknown_allocated_files,
    total_file_count,
    total_hard_link_alias_count,
    type_count
FROM category_aggregates;
""".strip()

NO_EXTENSION = 0
DOCUMENTS = 1
IMAGES = 2
DATA = 8
OTHER = 11


def _category(extension: str) -> int:
    extension = (extension or "").strip().lstrip(".").lower()
    if extension == "":
        return NO_EXTENSION
    if extension in {"txt", "pdf", "doc", "docx"}:
        return DOCUMENTS
    if extension in {"jpg", "jpeg", "png"}:
        return IMAGES
    if extension in {"bin", "dat", "db", "sqlite"}:
        return DATA
    return OTHER


def _db() -> sqlite3.Connection:
    connection = sqlite3.connect(":memory:")
    connection.create_function("fileop_category", 1, _category, deterministic=True)
    connection.execute(
        """
        CREATE TABLE files(
            path TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
            path_norm TEXT NOT NULL,
            parent_path TEXT NOT NULL,
            extension_norm TEXT NOT NULL,
            length INTEGER NOT NULL,
            allocated_length INTEGER NULL,
            is_directory INTEGER NOT NULL,
            volume_serial INTEGER NULL,
            file_reference INTEGER NULL
        ) WITHOUT ROWID;
        """
    )
    return connection


def _add(
    connection: sqlite3.Connection,
    path: str,
    parent: str,
    extension: str = "",
    logical: int = 0,
    allocated: int | None = 0,
    directory: bool = False,
    identity: tuple[int, int] | None = None,
) -> None:
    volume_serial, file_reference = identity or (None, None)
    connection.execute(
        """
        INSERT INTO files(
            path, path_norm, parent_path, extension_norm, length,
            allocated_length, is_directory, volume_serial, file_reference
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);
        """,
        (
            path,
            path.lower(),
            parent,
            extension.strip().lstrip(".").lower(),
            logical,
            allocated,
            int(directory),
            volume_serial,
            file_reference,
        ),
    )


def _query(connection: sqlite3.Connection, root: str, limit: int = 128) -> list[tuple]:
    return connection.execute(FILE_TYPE_SQL, {"root": root, "limit": limit}).fetchall()


def _split(rows: list[tuple]) -> tuple[list[tuple], list[tuple]]:
    return ([row for row in rows if row[0] == 0], [row for row in rows if row[0] == 1])


def _category_map(rows: list[tuple]) -> dict[int, tuple]:
    return {row[2]: row for row in rows}


def check_sql_semantics() -> int:
    cases = 0

    connection = _db()
    _add(connection, r"C:\Data\Images", r"C:\Data", directory=True)
    _add(connection, r"C:\Data\Images\one.JPG", r"C:\Data\Images", "JPG", 100, 128)
    _add(connection, r"C:\Data\Images\two.jpg", r"C:\Data\Images", "jpg", 200, 256)
    _add(connection, r"C:\Data\notes.txt", r"C:\Data", "txt", 50, 64)
    _add(connection, r"C:\Data\README", r"C:\Data", "", 25, 32)
    types, categories = _split(_query(connection, r"C:\Data", 10))
    by_ext = {row[1]: row for row in types}
    by_category = _category_map(categories)
    assert set(by_ext) == {"jpg", "txt", ""}
    assert by_ext["jpg"][3:8] == (300, 384, 0, 2, 0)
    assert by_category[IMAGES][3:9] == (300, 384, 0, 2, 0, 1)
    assert by_category[DOCUMENTS][3:9] == (50, 64, 0, 1, 0, 1)
    assert by_category[NO_EXTENSION][3:9] == (25, 32, 0, 1, 0, 1)
    assert types[0][9:15] == (375, 480, 0, 4, 0, 3)
    assert sum(row[3] for row in categories) == 375
    assert sum(row[8] for row in categories) == 3
    cases += 1

    connection = _db()
    identity = (0xAABB, 0x2233)
    _add(connection, r"C:\Data\A", r"C:\Data", directory=True)
    _add(connection, r"C:\Data\B", r"C:\Data", directory=True)
    _add(connection, r"C:\Data\A\shared.jpg", r"C:\Data\A", "jpg", 100, 128, identity=identity)
    _add(connection, r"C:\Data\B\shared.png", r"C:\Data\B", "png", 100, 128, identity=identity)
    types, categories = _split(_query(connection, r"C:\Data", 10))
    by_ext = {row[1]: row for row in types}
    images = _category_map(categories)[IMAGES]
    assert by_ext["jpg"][3:8] == (100, 128, 0, 1, 0)
    assert by_ext["png"][3:8] == (100, 0, 0, 1, 1)
    assert images[3:9] == (200, 128, 0, 2, 1, 2)
    assert images[9:15] == (200, 128, 0, 2, 1, 2)
    cases += 1

    connection = _db()
    _add(connection, r"C:\Data\a.bin", r"C:\Data", "bin", 300, 384)
    _add(connection, r"C:\Data\b.txt", r"C:\Data", "txt", 200, None)
    _add(connection, r"C:\Data\c.jpg", r"C:\Data", "jpg", 100, 128)
    types, categories = _split(_query(connection, r"C:\Data", 1))
    assert len(types) == 1 and types[0][1] == "bin"
    assert len(categories) == 3
    assert types[0][9] == 600
    assert types[0][11] == 1
    assert types[0][12] == 3
    assert types[0][14] == 3
    by_category = _category_map(categories)
    assert by_category[DATA][3:9] == (300, 384, 0, 1, 0, 1)
    assert by_category[DOCUMENTS][3:9] == (200, 0, 1, 1, 0, 1)
    assert by_category[IMAGES][3:9] == (100, 128, 0, 1, 0, 1)
    assert sum(row[3] for row in categories) == 600
    assert sum(row[6] for row in categories) == 3
    assert sum(row[8] for row in categories) == 3
    cases += 1

    connection = _db()
    _add(connection, r"C:\Data\inside.txt", r"C:\Data", "txt", 10, 16)
    _add(connection, r"C:\Database\sibling.txt", r"C:\Database", "txt", 999, 1024)
    types, categories = _split(_query(connection, r"C:\Data", 10))
    assert len(types) == 1 and types[0][3] == 10
    assert len(categories) == 1 and categories[0][3] == 10
    cases += 1

    connection = _db()
    assert _query(connection, r"C:\Empty", 10) == []
    cases += 1

    return cases


def check_repository(repo_root: Path) -> int:
    files = {
        "contract": repo_root / "src/FileOp.Core/Storage/IStorageAnalytics.cs",
        "classifier": repo_root / "src/FileOp.Core/Storage/StorageFileCategoryClassifier.cs",
        "sqlite": repo_root / "src/FileOp.Core/Storage/SqliteStorageAnalytics.cs",
        "memory": repo_root / "src/FileOp.Core/Search/InMemoryFileIndex.cs",
        "protocol": repo_root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs",
        "client": repo_root / "src/FileOp.Windows/IndexingService/IndexingServiceClient.cs",
        "dispatcher": repo_root / "src/FileOp.Windows/IndexingService/IndexingServiceDispatcher.cs",
        "backend": repo_root / "src/FileOp.Windows/IndexingService/NtfsIndexingServiceBackend.cs",
        "protocol_tests": repo_root / "tests/FileOp.Windows.Tests/IndexingStorageProtocolTests.cs",
        "parity_tests": repo_root / "tests/FileOp.Windows.Tests/StorageFileTypeParityTests.cs",
    }
    missing = [str(path) for path in files.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing repository files: " + ", ".join(missing))

    text = {name: path.read_text(encoding="utf-8") for name, path in files.items()}
    required = [
        ("contract", "StorageFileCategoryEntry"),
        ("contract", "IReadOnlyList<StorageFileCategoryEntry> Categories"),
        ("sqlite", "fileop_category(extension_norm)"),
        ("sqlite", "category_aggregates AS"),
        ("sqlite", "Categories = orderedCategories"),
        ("memory", "Categories = categories"),
        ("protocol", "public const int CurrentVersion = 8;"),
        ("protocol", "AnalyzeStorageTypes"),
        ("client", "AnalyzeStorageTypesAsync"),
        ("dispatcher", "IndexingServiceOperation.AnalyzeStorageTypes"),
        ("backend", "AnalyzeStorageTypesAsync"),
        ("protocol_tests", "NamedPipeRoundTripReturnsTypedStorageFileTypesAndExactCategories"),
        ("protocol_tests", "response.Analysis.Categories"),
        ("parity_tests", "MatchWithExactCategoriesWhenTypesAreTruncated"),
        ("parity_tests", "sqlite.Categories"),
    ]
    for name, needle in required:
        assert needle in text[name], f"{needle!r} missing from {files[name]}"

    switch_start = text["classifier"].find("return normalized switch")
    switch_end = text["classifier"].find("_ => StorageFileCategory.Other", switch_start)
    assert switch_start >= 0 and switch_end > switch_start, "Classifier switch could not be located"
    switch_text = text["classifier"][switch_start:switch_end]
    extensions = re.findall(r'"([^"\r\n]+)"', switch_text)
    duplicates = sorted(name for name, count in Counter(extensions).items() if count > 1)
    assert not duplicates, f"Duplicate extension switch patterns: {duplicates}"
    assert extensions.count("ts") == 1, ".ts must have exactly one deterministic category"

    marker = 'private const string FileTypeAnalysisSql = """'
    start = text["sqlite"].find(marker)
    assert start >= 0, "FileTypeAnalysisSql raw string marker is missing"
    start = text["sqlite"].find("\n", start) + 1
    end = text["sqlite"].find('\n        """;', start)
    assert end >= 0, "FileTypeAnalysisSql raw string terminator is missing"
    extracted = textwrap.dedent(text["sqlite"][start:end]).strip()
    assert extracted == FILE_TYPE_SQL, "Offline SQL verifier drifted from SqliteStorageAnalytics.FileTypeAnalysisSql"

    return len(required) + 4


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()

    sql_cases = check_sql_semantics()
    print(f"PASS storage file-type/category SQL semantics: {sql_cases} cases")

    if not args.self_test_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS storage file-type/category source wiring: {source_checks} checks")

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, sqlite3.Error) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
