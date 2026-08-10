#!/usr/bin/env python3
"""Zero-Actions checks for FileOp storage-history service protocol semantics."""
from __future__ import annotations

import argparse
import random
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path


def _hour_bucket(value: datetime) -> datetime:
    utc = value.astimezone(timezone.utc)
    return utc.replace(minute=0, second=0, microsecond=0)


def check_hourly_bucket(cases: int = 2000) -> int:
    rng = random.Random(20260808)
    for _ in range(cases):
        offset_minutes = rng.randrange(-12 * 60, 14 * 60 + 1, 15)
        offset = timezone(timedelta(minutes=offset_minutes))
        value = datetime(
            rng.randint(2000, 2040),
            rng.randint(1, 12),
            rng.randint(1, 28),
            rng.randint(0, 23),
            rng.randint(0, 59),
            rng.randint(0, 59),
            rng.randint(0, 999999),
            tzinfo=offset,
        )
        bucket = _hour_bucket(value)
        assert bucket.tzinfo == timezone.utc
        assert bucket.minute == 0 and bucket.second == 0 and bucket.microsecond == 0
        assert bucket <= value.astimezone(timezone.utc) < bucket + timedelta(hours=1)

    known = datetime(2026, 8, 8, 16, 22, 47, tzinfo=timezone(timedelta(hours=10)))
    assert _hour_bucket(known) == datetime(2026, 8, 8, 6, 0, 0, tzinfo=timezone.utc)
    return cases + 1


def _method_body(source: str, signature: str, next_signature: str) -> str:
    start = source.index(signature)
    end = source.index(next_signature, start)
    return source[start:end]


def check_repository(repo_root: Path) -> int:
    protocol_path = repo_root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs"
    policy_path = repo_root / "src/FileOp.Core/Storage/StorageHistoryCapturePolicy.cs"
    backend_path = repo_root / "src/FileOp.Windows/IndexingService/StorageHistoryIndexingServiceBackend.cs"
    paged_backend_path = repo_root / "src/FileOp.Windows/IndexingService/PagedDirectoryIndexingServiceBackend.cs"
    outer_backend_path = repo_root / "src/FileOp.Windows/IndexingService/StorageOptimizationIndexingServiceBackend.cs"
    native_backend_path = repo_root / "src/FileOp.Windows/IndexingService/NtfsIndexingServiceBackend.cs"
    client_path = repo_root / "src/FileOp.Windows/IndexingService/IndexingServiceClient.cs"
    dispatcher_path = repo_root / "src/FileOp.Windows/IndexingService/IndexingServiceDispatcher.cs"
    program_path = repo_root / "src/FileOp.Indexer/Program.cs"
    tests_path = repo_root / "tests/FileOp.Windows.Tests/IndexingStorageHistoryProtocolTests.cs"
    paths = [
        protocol_path,
        policy_path,
        backend_path,
        paged_backend_path,
        outer_backend_path,
        native_backend_path,
        client_path,
        dispatcher_path,
        program_path,
        tests_path,
    ]
    missing = [str(path) for path in paths if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing repository files: " + ", ".join(missing))

    protocol = protocol_path.read_text(encoding="utf-8")
    policy = policy_path.read_text(encoding="utf-8")
    backend = backend_path.read_text(encoding="utf-8")
    paged_backend = paged_backend_path.read_text(encoding="utf-8")
    outer_backend = outer_backend_path.read_text(encoding="utf-8")
    native_backend = native_backend_path.read_text(encoding="utf-8")
    client = client_path.read_text(encoding="utf-8")
    dispatcher = dispatcher_path.read_text(encoding="utf-8")
    program = program_path.read_text(encoding="utf-8")
    tests = tests_path.read_text(encoding="utf-8")

    required = [
        (protocol, "public const int CurrentVersion = 7;"),
        (protocol, "CaptureStorageHistory,"),
        (protocol, "GetStorageHistory,"),
        (protocol, "IndexingStorageHistoryCaptureRequest"),
        (protocol, "IndexingStorageHistoryQueryRequest"),
        (policy, "GetHourlyBucket"),
        (backend, "StorageHistoryCapturePolicy.GetHourlyBucket(_utcNow())"),
        (backend, "MaxTypes: 1"),
        (backend, "history.SaveSnapshotAsync("),
        (backend, "using Microsoft.Data.Sqlite;"),
        (backend, "exception.SqliteErrorCode is 5 or 6"),
        (client, "CaptureStorageHistoryAsync("),
        (client, "GetStorageHistoryAsync("),
        (dispatcher, "DeserializeStorageHistoryCapture"),
        (dispatcher, "DeserializeStorageHistoryQuery"),
        (dispatcher, 'throw new JsonException("limit must be between 1 and 4096.")'),
        (program, "new StorageOptimizationIndexingServiceBackend(databaseDirectory)"),
        (outer_backend, "_inner = new PagedDirectoryIndexingServiceBackend(_databaseDirectory)"),
        (outer_backend, "_inner.CaptureStorageHistoryAsync(request, cancellationToken)"),
        (outer_backend, "_inner.GetStorageHistoryAsync(request, cancellationToken)"),
        (paged_backend, "_inner = new StorageHistoryIndexingServiceBackend(_databaseDirectory)"),
        (tests, "NamedPipeRoundTripPreservesCaptureAndHistoryPayloads"),
        (tests, "CapturePolicyUsesUtcHourlyBuckets"),
    ]
    for text, needle in required:
        assert needle in text, f"required service invariant missing: {needle}"

    capture_record = re.search(
        r"public sealed record IndexingStorageHistoryCaptureRequest\((.*?)\);",
        protocol,
        flags=re.S,
    )
    assert capture_record is not None
    capture_fields = capture_record.group(1)
    assert "DateTime" not in capture_fields and "Captured" not in capture_fields, (
        "capture time must be service-owned rather than supplied by the client"
    )

    capture_body = _method_body(
        backend,
        "public async ValueTask<IndexingStorageHistoryCaptureResponse> CaptureStorageHistoryAsync(",
        "public async ValueTask<IndexingStorageHistoryQueryResponse> GetStorageHistoryAsync(",
    )
    assert "_inner.AnalyzeStorageTypesAsync(" in capture_body
    assert "SaveSnapshotAsync(" in capture_body
    assert capture_body.index("AnalyzeStorageTypesAsync(") < capture_body.index("SaveSnapshotAsync(")

    query_body = _method_body(
        backend,
        "public async ValueTask<IndexingStorageHistoryQueryResponse> GetStorageHistoryAsync(",
        "public void Dispose()",
    )
    assert "GetSnapshotsAsync(" in query_body
    assert "AnalyzeStorage" not in query_body
    assert "Checkpoint" not in query_body

    busy_catch = "catch (SqliteException exception) when (IsDatabaseBusy(exception))"
    assert backend.count(busy_catch) == 2, (
        "capture and query must both translate SQLite contention into retryable Busy responses"
    )
    assert "IndexingServiceErrorCode.Busy" in backend
    assert "canRetry: true" in backend

    # Keep every wrapper's database-key formula aligned with the reviewed native backend.
    for needle in [
        'new string(root.Where(static character => char.IsLetterOrDigit(character)).ToArray())',
        'rootToken = "root";',
        'ntfs-{volumeIdentity:X16}-{rootToken.ToLowerInvariant()}',
    ]:
        assert needle in backend, f"history database-key formula missing: {needle}"
        assert needle in paged_backend, f"paged-browse database-key formula missing: {needle}"
        assert needle in outer_backend, f"optimization database-key formula missing: {needle}"
    assert 'ntfs-{volume.VolumeIdentity:X16}-{rootToken.ToLowerInvariant()}' in native_backend

    assert "CaptureStorageHistoryAsync" not in native_backend, (
        "history integration should remain in the wrapper, not alter reviewed NTFS lifecycle code"
    )
    return len(required) + 18


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=2000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    bucket_cases = check_hourly_bucket(args.cases)
    print(f"PASS storage history hourly buckets: {bucket_cases} cases")
    if not args.self_test_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS storage history service wiring: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)