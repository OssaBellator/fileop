#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's exact paged directory browse contract."""
from __future__ import annotations

import argparse
import random
import sqlite3
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Entry:
    is_directory: bool
    name: str
    path: str


@dataclass(frozen=True)
class Cursor:
    is_directory: bool
    name: str
    path: str


def _key(entry: Entry) -> tuple[int, str, str]:
    return (0 if entry.is_directory else 1, entry.name.lower(), entry.path.lower())


def _make_db(entries: list[Entry]) -> sqlite3.Connection:
    connection = sqlite3.connect(":memory:")
    connection.execute(
        """
        CREATE TABLE files(
            path TEXT PRIMARY KEY COLLATE NOCASE,
            path_norm TEXT NOT NULL,
            name TEXT NOT NULL,
            name_norm TEXT NOT NULL,
            parent_path TEXT NOT NULL,
            is_directory INTEGER NOT NULL,
            parent_volume_serial INTEGER NULL,
            parent_file_reference INTEGER NULL
        );
        """
    )
    connection.execute(
        "INSERT INTO files VALUES (?, ?, ?, ?, ?, 1, NULL, NULL)",
        (r"C:\Root", r"c:\root", "Root", "root", r"C:\"),
    )
    connection.executemany(
        "INSERT INTO files VALUES (?, ?, ?, ?, ?, ?, 1, 100)",
        [
            (
                entry.path,
                entry.path.lower(),
                entry.name,
                entry.name.lower(),
                r"C:\Root",
                1 if entry.is_directory else 0,
            )
            for entry in entries
        ],
    )
    return connection


def _read_page(
    connection: sqlite3.Connection,
    page_size: int,
    cursor: Cursor | None,
) -> tuple[list[Entry], Cursor | None]:
    cursor_sql = ""
    parameters: dict[str, object] = {"take": page_size + 1}
    if cursor is not None:
        cursor_sql = """
            AND (
                is_directory < :cursor_is_directory
                OR (
                    is_directory = :cursor_is_directory
                    AND (
                        name_norm > :cursor_name_norm
                        OR (name_norm = :cursor_name_norm AND path_norm > :cursor_path_norm)
                    )
                )
            )
        """
        parameters.update(
            cursor_is_directory=1 if cursor.is_directory else 0,
            cursor_name_norm=cursor.name.lower(),
            cursor_path_norm=cursor.path.lower(),
        )

    rows = connection.execute(
        f"""
        SELECT is_directory, name, path
        FROM files
        WHERE parent_volume_serial = 1
          AND parent_file_reference = 100
          {cursor_sql}
        ORDER BY is_directory DESC, name_norm ASC, path_norm ASC
        LIMIT :take;
        """,
        parameters,
    ).fetchall()
    entries = [Entry(bool(row[0]), row[1], row[2]) for row in rows]
    next_cursor = None
    if len(entries) > page_size:
        entries.pop()
        last = entries[-1]
        next_cursor = Cursor(last.is_directory, last.name, last.path)
    return entries, next_cursor


def check_keyset_properties(cases: int = 10_000) -> int:
    rng = random.Random(20260808)
    alphabet = "abcdefghijklmnopqrstuvwxyz"
    checks = 0
    for case in range(cases):
        count = rng.randint(0, 180)
        entries: list[Entry] = []
        used: set[str] = set()
        while len(entries) < count:
            name = "".join(rng.choice(alphabet) for _ in range(rng.randint(1, 10)))
            if rng.random() < 0.6:
                name += rng.choice([".txt", ".bin", ".jpg", ".zip", ""])
            suffix = rng.randrange(1_000_000)
            path = rf"C:\Root\{name}-{suffix}"
            if path.lower() in used:
                continue
            used.add(path.lower())
            entries.append(Entry(bool(rng.getrandbits(1)), name, path))

        expected = sorted(entries, key=_key)
        page_size = rng.randint(1, 32)
        connection = _make_db(entries)
        try:
            actual: list[Entry] = []
            cursor: Cursor | None = None
            while True:
                page, cursor = _read_page(connection, page_size, cursor)
                actual.extend(page)
                if cursor is None:
                    break
            assert actual == expected
            assert len({entry.path.lower() for entry in actual}) == len(actual)
            checks += 2

            if expected:
                first_page, first_cursor = _read_page(connection, max(1, min(page_size, len(expected))), None)
                if first_cursor is not None:
                    before = Entry(True, "000-before", rf"C:\Root\000-before-{case}")
                    connection.execute(
                        "INSERT INTO files VALUES (?, ?, ?, ?, ?, 1, 1, 100)",
                        (before.path, before.path.lower(), before.name, before.name.lower(), r"C:\Root"),
                    )
                    second_page, _ = _read_page(connection, page_size, first_cursor)
                    first_paths = {entry.path.lower() for entry in first_page}
                    assert first_paths.isdisjoint(entry.path.lower() for entry in second_page)
                    checks += 1
        finally:
            connection.close()

    return checks


def check_repository(repo_root: Path) -> int:
    protocol_path = repo_root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs"
    model_path = repo_root / "src/FileOp.Core/Models/FileDirectoryBrowsePage.cs"
    interface_path = repo_root / "src/FileOp.Core/Search/IFileDirectoryBrowser.cs"
    sqlite_path = repo_root / "src/FileOp.Core/Search/SqliteFileDirectoryBrowser.cs"
    backend_interface_path = repo_root / "src/FileOp.Windows/IndexingService/IIndexingServiceBackend.cs"
    backend_path = repo_root / "src/FileOp.Windows/IndexingService/PagedDirectoryIndexingServiceBackend.cs"
    client_path = repo_root / "src/FileOp.Windows/IndexingService/IndexingServiceClient.cs"
    dispatcher_path = repo_root / "src/FileOp.Windows/IndexingService/IndexingServiceDispatcher.cs"
    program_path = repo_root / "src/FileOp.Indexer/Program.cs"
    engine_path = repo_root / "src/FileOp.App/DesktopSearchEngine.DirectoryBrowse.cs"
    native_backend_path = repo_root / "src/FileOp.Windows/IndexingService/NtfsIndexingServiceBackend.cs"
    tests_path = repo_root / "tests/FileOp.Windows.Tests/IndexingDirectoryBrowseProtocolTests.cs"

    paths = [
        protocol_path,
        model_path,
        interface_path,
        sqlite_path,
        backend_interface_path,
        backend_path,
        client_path,
        dispatcher_path,
        program_path,
        engine_path,
        native_backend_path,
        tests_path,
    ]
    missing = [str(path) for path in paths if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing repository files: " + ", ".join(missing))

    texts = {path: path.read_text(encoding="utf-8") for path in paths}
    protocol = texts[protocol_path]
    sqlite = texts[sqlite_path]
    backend_interface = texts[backend_interface_path]
    backend = texts[backend_path]
    client = texts[client_path]
    dispatcher = texts[dispatcher_path]
    program = texts[program_path]
    engine = texts[engine_path]
    native_backend = texts[native_backend_path]
    tests = texts[tests_path]

    required = [
        (protocol, "public const int CurrentVersion = 6;"),
        (protocol, "BrowseDirectory,"),
        (protocol, "IndexingDirectoryBrowseRequest"),
        (protocol, "FileDirectoryBrowseCursor? Cursor = null"),
        (protocol, "IndexingDirectoryBrowseResponse"),
        (backend_interface, "BrowseDirectoryAsync("),
        (client, "IndexingServiceOperation.BrowseDirectory"),
        (dispatcher, "DeserializeDirectoryBrowse"),
        (dispatcher, 'throw new JsonException("pageSize must be between 1 and 1024.")'),
        (dispatcher, "The directory browse cursor does not belong to the requested directory."),
        (backend, "IndexingVolumeFileGate(databasePath)"),
        (backend, "TryAcquireRead()"),
        (backend, "HasCheckpointAsync(sourceKey"),
        (backend, "DirectoryExistsAsync(request.DirectoryPath"),
        (backend, "SqliteFileDirectoryBrowser(databasePath)"),
        (sqlite, "Mode = SqliteOpenMode.ReadOnly"),
        (sqlite, "OR EXISTS("),
        (sqlite, "parent_volume_serial = @parent_volume_serial"),
        (sqlite, "parent_file_reference = @parent_file_reference"),
        (sqlite, "is_directory < @cursor_is_directory"),
        (sqlite, "name_norm > @cursor_name_norm"),
        (sqlite, "path_norm > @cursor_path_norm"),
        (sqlite, "ORDER BY is_directory DESC, name_norm ASC, path_norm ASC"),
        (sqlite, "LIMIT @take"),
        (sqlite, "checked(pageSize + 1)"),
        (program, "new PagedDirectoryIndexingServiceBackend(databaseDirectory)"),
        (engine, "_searchOperationGate.WaitAsync(_lifetimeCancellation.Token)"),
        (engine, "_nativeOperationGate.WaitAsync(_lifetimeCancellation.Token)"),
        (engine, "session.Client.BrowseDirectoryAsync("),
        (engine, "ResponseTooLarge"),
        (engine, "_fallbackIndex.SearchAsync("),
        (engine, "FileSearchQuery.Parse(string.Empty, int.MaxValue)"),
        (engine, "!string.IsNullOrWhiteSpace(record.ParentPath)"),
        (engine, "OrderByDescending(static record => record.IsDirectory)"),
        (tests, "DispatcherRejectsCursorFromAnotherDirectoryBeforeBackendCall"),
        (tests, "NamedPipeRoundTripPreservesBrowseCursorAndEntries"),
    ]
    for source, needle in required:
        assert needle in source, f"required directory-browse invariant missing: {needle}"

    forbidden = [
        "new SqliteFileIndex(databasePath)",
        "SqliteOpenMode.ReadWriteCreate",
        "CREATE TABLE",
        "CREATE INDEX",
        "Directory.Enumerate",
        "Directory.GetFiles",
        "Directory.GetDirectories",
        "FileSystemWatcher",
    ]
    for needle in forbidden[:4]:
        assert needle not in backend + sqlite, f"browse service must remain read-only: {needle}"
    for needle in forbidden[4:]:
        assert needle not in engine, f"desktop browse must not rescan the filesystem: {needle}"

    assert "BrowseDirectoryAsync" not in native_backend, (
        "paged browsing should remain in its wrapper rather than disturb the reviewed NTFS lifecycle backend"
    )
    assert "CancellationTokenSource" not in engine
    assert "_lifetimeCancellation.Token" in engine
    return len(required) + len(forbidden) + 3


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=10_000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = check_keyset_properties(args.cases)
    print(f"PASS paged directory keyset properties: {checks} checks")
    if not args.self_test_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS paged directory browse source wiring: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, sqlite3.Error, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
