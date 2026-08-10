#!/usr/bin/env python3
"""Verify FileOp durable-checkpoint/live-USN freshness semantics without GitHub Actions."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

LONG_MAX = (1 << 63) - 1


@dataclass(frozen=True)
class Freshness:
    identity_matches: bool
    within_window: bool
    below_retention_floor: bool
    ahead_of_journal: bool
    backlog: int | None
    headroom: int | None


def saturating_distance(high: int, low: int) -> int:
    if high < low:
        return 0
    return min(LONG_MAX, high - low)


def analyze(
    durable_journal_id: int,
    durable_next_usn: int,
    live_journal_id: int,
    lowest_valid_usn: int,
    live_next_usn: int,
) -> Freshness:
    identity_matches = durable_journal_id == live_journal_id
    within = identity_matches and lowest_valid_usn <= durable_next_usn <= live_next_usn
    below = identity_matches and durable_next_usn < lowest_valid_usn
    ahead = identity_matches and durable_next_usn > live_next_usn
    return Freshness(
        identity_matches,
        within,
        below,
        ahead,
        saturating_distance(live_next_usn, durable_next_usn) if within else None,
        saturating_distance(durable_next_usn, lowest_valid_usn) if within else None,
    )


def run_model(cases: int) -> int:
    checks = 0
    assert analyze(7, 900, 7, 500, 1_000) == Freshness(True, True, False, False, 100, 400)
    assert analyze(1, 900, 2, 500, 1_000) == Freshness(False, False, False, False, None, None)
    assert analyze(1, 400, 1, 500, 1_000) == Freshness(True, False, True, False, None, None)
    assert analyze(1, 1_100, 1, 500, 1_000) == Freshness(True, False, False, True, None, None)
    checks += 4

    rng = random.Random(20260810)
    for _ in range(cases):
        lowest = rng.randint(0, 10**12)
        live = lowest + rng.randint(0, 10**9)
        mode = rng.randrange(4)
        durable_id = rng.getrandbits(64)
        live_id = durable_id

        if mode == 0:
            durable = rng.randint(lowest, live)
        elif mode == 1 and lowest > 0:
            durable = lowest - rng.randint(1, min(lowest, 10**6))
        elif mode == 2:
            durable = live + rng.randint(1, 10**6)
        else:
            durable = rng.randint(0, 10**12)
            live_id = durable_id ^ 1

        result = analyze(durable_id, durable, live_id, lowest, live)
        identity = durable_id == live_id
        expected_within = identity and lowest <= durable <= live
        assert result.within_window == expected_within
        assert result.below_retention_floor == (identity and durable < lowest)
        assert result.ahead_of_journal == (identity and durable > live)
        assert (result.backlog is not None) == expected_within
        assert (result.headroom is not None) == expected_within
        if expected_within:
            assert result.backlog + result.headroom == live - lowest
        else:
            assert result.backlog is None and result.headroom is None
        checks += 7

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "model": root / "src/FileOp.Core/Performance/IndexDatabaseDiagnostics.cs",
        "backend": root / "src/FileOp.Windows/IndexingService/StorageOptimizationIndexingServiceBackend.cs",
        "view": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml",
        "view_code": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs",
        "reader_test": root / "tests/FileOp.Windows.Tests/IndexDatabaseDiagnosticsTests.cs",
        "protocol_test": root / "tests/FileOp.Windows.Tests/IndexingIndexDiagnosticsProtocolTests.cs",
        "protocol": root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs",
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
        "IndexJournalCheckpointDiagnostics",
        "IndexJournalFreshnessDiagnostics",
        "JournalIdentityMatches",
        "CheckpointWithinReadableWindow",
        "CheckpointBelowRetentionFloor",
        "CheckpointAheadOfJournal",
        "BacklogUsnDistance",
        "RetentionHeadroomUsnDistance",
        "AgeAt(DateTimeOffset capturedAt)",
        "ReadWithCheckpointAsync",
        "SELECT generation, position, updated_utc_ticks",
        "FROM source_checkpoints",
        "FromSqlInteger(reader.GetInt64(0))",
    ):
        assert needle in text["model"], needle
        checks += 1

    for needle in (
        "ReadWithCheckpointAsync(",
        "new NtfsUsnJournal().Query(volume)",
        "journal.LowestValidUsn",
        "journal.NextUsn",
        "catch (Win32Exception)",
        "catch (UnauthorizedAccessException)",
        "JournalFreshness = new IndexJournalFreshnessDiagnostics",
    ):
        assert needle in text["backend"], needle
        checks += 1

    for needle in (
        "Checkpoint age",
        "Journal identity",
        "USN backlog distance",
        "Retention headroom",
        "USN distances are sequence-position differences, not file/event counts, bytes, or elapsed time",
    ):
        assert needle in text["view"], needle
        checks += 1

    for needle in (
        "ApplyJournalFreshness",
        "The missing journal range can no longer be replayed incrementally",
        "differs from the live journal ID",
        "is ahead of the live journal head",
        "Backlog and retention headroom are USN sequence-position distances",
        "Older protocol-v8 helpers may omit this optional evidence",
    ):
        assert needle in text["view_code"], needle
        checks += 1

    for needle in (
        "ReaderPreservesDurableCheckpointIdentityPositionAndTimestamp",
        "FreshnessReportsReadableWindowAsUsnDistances",
        "FreshnessDoesNotInventBacklogForInvalidJournalContinuity",
        "0xFEDCBA9876543210UL",
    ):
        assert needle in text["reader_test"], needle
        checks += 1

    for needle in (
        "NamedPipeRoundTripPreservesIndexDatabaseAndJournalEvidence",
        "JournalFreshness.JournalIdentityMatches",
        "JournalFreshness.BacklogUsnDistance",
        "JournalFreshness.RetentionHeadroomUsnDistance",
    ):
        assert needle in text["protocol_test"], needle
        checks += 1

    assert "public const int CurrentVersion = 8;" in text["protocol"]
    assert "GetIndexDiagnostics" in text["protocol"]
    assert "GetJournalFreshness" not in text["protocol"]
    checks += 3

    freshness_source = "\n".join(text[name] for name in ("model", "backend", "view", "view_code"))
    for forbidden in (
        "ReadNextBatchAsync",
        "FSCTL_READ_USN_JOURNAL",
        "SaveCheckpointAsync",
        "DeleteCheckpointAsync",
        "VACUUM",
        "wal_checkpoint",
        "File.Delete(",
        "Directory.Delete(",
        "Registry.",
        "ServiceController",
    ):
        assert forbidden not in freshness_source, forbidden
        checks += 1

    assert "verify_usn_freshness.py" in text["gate"]
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
    suffix = f" and {repository_checks:,} source/UI checks" if args.repo_root else ""
    print(
        "PASS: USN/checkpoint freshness verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
