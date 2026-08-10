#!/usr/bin/env python3
"""Verify FileOp helper-owned index diagnostics without GitHub Actions."""
from __future__ import annotations

import argparse
import random
import sqlite3
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

LONG_MAX = (1 << 63) - 1


def saturating_add(left: int, right: int) -> int:
    left = max(0, left)
    right = max(0, right)
    return LONG_MAX if left > LONG_MAX - right else left + right


def saturating_multiply(left: int, right: int) -> int:
    if left <= 0 or right <= 0:
        return 0
    return LONG_MAX if left > LONG_MAX // right else left * right


def derived_metrics(
    database: int,
    wal: int,
    shm: int,
    page_size: int,
    page_count: int,
    free_pages: int,
    cache_setting: int,
) -> tuple[int, int, int, int, float | None, int | None]:
    footprint = saturating_add(saturating_add(database, wal), shm)
    logical = saturating_multiply(page_size, page_count)
    reusable = saturating_multiply(page_size, free_pages)
    live = max(0, logical - reusable)
    reusable_percent = None if page_count <= 0 else min(100.0, max(0.0, free_pages * 100.0 / page_count))
    if cache_setting == 0:
        cache_default = None
    elif cache_setting < 0:
        cache_default = saturating_multiply(abs(cache_setting), 1024)
    else:
        cache_default = saturating_multiply(cache_setting, page_size)
    return footprint, logical, reusable, live, reusable_percent, cache_default


def run_model(cases: int) -> int:
    checks = 0
    assert derived_metrics(1000, 200, 50, 4096, 100, 25, -2000) == (
        1250,
        409600,
        102400,
        307200,
        25.0,
        2048000,
    )
    assert derived_metrics(-10, 20, -30, 4096, 0, 0, 0)[0] == 20
    assert saturating_add(LONG_MAX, 1) == LONG_MAX
    assert saturating_multiply(LONG_MAX, 2) == LONG_MAX
    checks += 4

    rng = random.Random(20260810)
    for _ in range(cases):
        database = rng.randint(0, 2**48)
        wal = rng.randint(0, 2**42)
        shm = rng.randint(0, 2**30)
        page_size = rng.choice([512, 1024, 2048, 4096, 8192, 16384, 32768, 65536])
        page_count = rng.randint(0, 10_000_000)
        free_pages = rng.randint(0, page_count) if page_count else 0
        cache_setting = rng.choice([-2000, -8192, 0, 128, 512, 2000])
        footprint, logical, reusable, live, percent, cache_default = derived_metrics(
            database,
            wal,
            shm,
            page_size,
            page_count,
            free_pages,
            cache_setting,
        )
        assert 0 <= footprint <= LONG_MAX
        assert 0 <= logical <= LONG_MAX
        assert 0 <= reusable <= logical
        assert live == logical - reusable
        assert percent is None if page_count == 0 else 0.0 <= percent <= 100.0
        assert cache_default is None if cache_setting == 0 else 0 < cache_default <= LONG_MAX
        checks += 6
    return checks


def run_sqlite_fixture() -> int:
    with tempfile.TemporaryDirectory(prefix="fileop-index-diag-") as temp:
        path = Path(temp) / "index.sqlite"
        connection = sqlite3.connect(path)
        try:
            mode = connection.execute("PRAGMA journal_mode=WAL").fetchone()[0]
            connection.executescript(
                """
                CREATE TABLE index_metadata(id INTEGER PRIMARY KEY, item_count INTEGER NOT NULL);
                INSERT INTO index_metadata(id, item_count) VALUES (1, 3);
                CREATE TABLE files(path TEXT PRIMARY KEY, size INTEGER NOT NULL) WITHOUT ROWID;
                INSERT INTO files(path, size) VALUES ('a', 1), ('b', 2), ('c', 3);
                """
            )
            connection.commit()
            page_size = int(connection.execute("PRAGMA page_size").fetchone()[0])
            page_count = int(connection.execute("PRAGMA page_count").fetchone()[0])
            free_pages = int(connection.execute("PRAGMA freelist_count").fetchone()[0])
            cache_setting = int(connection.execute("PRAGMA cache_size").fetchone()[0])
            item_count = int(connection.execute("SELECT item_count FROM index_metadata WHERE id=1").fetchone()[0])
            assert str(mode).lower() == "wal"
            assert page_size > 0
            assert page_count > 0
            assert 0 <= free_pages <= page_count
            assert cache_setting != 0
            assert item_count == 3
        finally:
            connection.close()
    return 6


def check_repository(root: Path) -> int:
    paths = {
        "model": root / "src/FileOp.Core/Performance/IndexDatabaseDiagnostics.cs",
        "resolver": root / "src/FileOp.Windows/IndexingService/IndexDatabasePathResolver.cs",
        "native": root / "src/FileOp.Windows/IndexingService/NtfsIndexingServiceBackend.cs",
        "history": root / "src/FileOp.Windows/IndexingService/StorageHistoryIndexingServiceBackend.cs",
        "browse": root / "src/FileOp.Windows/IndexingService/PagedDirectoryIndexingServiceBackend.cs",
        "backend": root / "src/FileOp.Windows/IndexingService/StorageOptimizationIndexingServiceBackend.cs",
        "protocol": root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs",
        "interface": root / "src/FileOp.Windows/IndexingService/IIndexingServiceBackend.cs",
        "client": root / "src/FileOp.Windows/IndexingService/IndexingServiceClient.cs",
        "dispatcher": root / "src/FileOp.Windows/IndexingService/IndexingServiceDispatcher.cs",
        "engine": root / "src/FileOp.App/DesktopSearchEngine.PerformanceDiagnostics.cs",
        "view": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml",
        "view_code": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs",
        "reader_test": root / "tests/FileOp.Windows.Tests/IndexDatabaseDiagnosticsTests.cs",
        "protocol_test": root / "tests/FileOp.Windows.Tests/IndexingIndexDiagnosticsProtocolTests.cs",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    ET.fromstring(text["view"])
    checks = 1

    for needle in (
        "IndexDatabaseDiagnostics",
        "SqliteIndexDatabaseDiagnosticsReader",
        "SqliteOpenMode.ReadOnly",
        "PRAGMA query_only = ON",
        '"page_size"',
        '"page_count"',
        '"freelist_count"',
        '"cache_size"',
        '"journal_mode"',
        'SELECT item_count FROM index_metadata WHERE id = 1;',
        'ReadOptionalFileLength(_databasePath + "-wal")',
        'ReadOptionalFileLength(_databasePath + "-shm")',
        "ReaderCacheDefaultTargetBytes",
        "ReusableFreePageBytes",
        "Math.Max(0, DatabaseFileBytes)",
        "FileOp currently does not set PRAGMA cache_size",
    ):
        assert needle in text["model"], needle
        checks += 1

    for needle in (
        "public const int CurrentVersion = 8;",
        "GetIndexDiagnostics",
        "IndexingIndexDiagnosticsRequest",
        "IndexingIndexDiagnosticsResponse",
    ):
        assert needle in text["protocol"], needle
        checks += 1

    for needle in (
        "GetIndexDiagnosticsAsync",
        "TryAcquireRead",
        "HasCheckpointAsync",
        "SqliteIndexDatabaseDiagnosticsReader",
        "ReadWithCheckpointAsync",
        "IndexDatabasePathResolver.CreatePath",
    ):
        assert needle in text["backend"], needle
        checks += 1

    assert "GetIndexDiagnosticsAsync" in text["interface"]
    assert "IndexingServiceOperation.GetIndexDiagnostics" in text["client"]
    assert "DeserializeIndexDiagnostics" in text["dispatcher"]
    checks += 3

    for name in ("native", "history", "browse", "backend"):
        assert "IndexDatabasePathResolver" in text[name], f"{name} must use shared database resolver"
        checks += 1

    for needle in (
        'return $"ntfs-{volumeIdentity:X16}-{rootToken.ToLowerInvariant()}";',
        'return Path.Combine(directory, $"{CreateKey(volumeIdentity, volumeRootPath)}.sqlite");',
    ):
        assert needle in text["resolver"], needle
        checks += 1

    for needle in (
        "CaptureNativeIndexDatabaseDiagnosticsAsync",
        "GetIndexDiagnosticsAsync(",
        "Index database probe",
        "IndexDatabaseStatus",
        "IndexingServiceErrorCode.Busy",
        "IndexingServiceErrorCode.SnapshotRequired",
    ):
        assert needle in text["engine"], needle
        checks += 1

    for needle in (
        "FileOp index footprint",
        "Helper files",
        "Reusable pages",
        "Reader cache default",
        "connection setting, not observed RAM usage",
    ):
        assert needle in text["view"], needle
        checks += 1

    for needle in (
        "Reusable pages can be reused by SQLite",
        "reader cache default is not observed resident memory",
        "ReusableFreePagePercent",
        "ReaderCacheDefaultTargetBytes",
    ):
        assert needle in text["view_code"], needle
        checks += 1

    for needle in (
        "ReaderReportsHelperFileAndPageEvidence",
        "DerivedMetricsStayConservativeAndDoNotClaimResidentCache",
        "DerivedFileFootprintClampsMalformedNegativeInputs",
        "SharedResolverPreservesHistoricalDatabaseKeyFormat",
        '"ntfs-0000000000001234-c"',
    ):
        assert needle in text["reader_test"], needle
        checks += 1

    for needle in (
        "DispatcherNormalizesIndexDiagnosticsRootBeforeBackendCall",
        "NamedPipeRoundTripPreservesIndexDatabaseAndJournalEvidence",
        "ReaderCacheDefaultTargetBytes",
        "JournalFreshness.BacklogUsnDistance",
    ):
        assert needle in text["protocol_test"], needle
        checks += 1

    diagnostics_source = "\n".join(
        text[name]
        for name in ("model", "backend", "engine", "view", "view_code")
    )
    for forbidden in (
        "VACUUM",
        "wal_checkpoint",
        "DELETE FROM",
        "INSERT INTO",
        "UPDATE ",
        "File.Delete(",
        "Directory.Delete(",
        "Registry.",
        "ServiceController",
        "EmptyWorkingSet",
    ):
        assert forbidden not in diagnostics_source, forbidden
        checks += 1

    assert "verify_index_diagnostics.py" in text["gate"]
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
    fixture_checks = run_sqlite_fixture()
    repository_checks = 0
    if args.repo_root is not None:
        repository_checks = check_repository(args.repo_root.resolve())
    suffix = f", {fixture_checks:,} SQLite fixture checks"
    if args.repo_root is not None:
        suffix += f" and {repository_checks:,} source/UI checks"
    print(
        "PASS: index diagnostics verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
