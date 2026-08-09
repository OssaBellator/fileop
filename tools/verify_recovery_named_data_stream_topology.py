#!/usr/bin/env python3
"""Zero-Actions model/source checks for Copy named-$DATA-stream topology evidence."""
from __future__ import annotations

import argparse
import hashlib
import random
import sqlite3
import struct
from pathlib import Path
from typing import Iterable, List, Sequence, Tuple

PREFIX = b"FileOp.NamedDataStreams.v1\0"
FORMAT_VERSION = 1


def topology_evidence(entries: Sequence[Tuple[str, int]]) -> Tuple[int, int, str]:
    ordered = sorted(entries, key=lambda entry: entry[0])
    if len({name for name, _ in ordered}) != len(ordered):
        raise ValueError("duplicate stream name")
    digest = hashlib.sha256()
    digest.update(PREFIX)
    digest.update(struct.pack("<i", len(ordered)))
    for name, size in ordered:
        if not name.startswith(":") or not name.upper().endswith(":$DATA") or name.upper() == "::$DATA":
            raise ValueError("not a named $DATA stream")
        if size < 0:
            raise ValueError("negative stream size")
        encoded = name.encode("utf-8")
        digest.update(struct.pack("<i", len(encoded)))
        digest.update(encoded)
        digest.update(struct.pack("<q", size))
    return FORMAT_VERSION, len(ordered), digest.hexdigest()


def random_stream_name(rng: random.Random, index: int) -> str:
    alphabet = "abcdefghijklmnopqrstuvwxyz0123456789_-"
    token = "".join(rng.choice(alphabet) for _ in range(rng.randint(1, 24)))
    return ":{}-{}:$DATA".format(token, index)


def run_model(cases: int) -> int:
    rng = random.Random(20260809)
    checks = 0
    for _ in range(cases):
        count = rng.randint(0, 8)
        topology: List[Tuple[str, int]] = [
            (random_stream_name(rng, index), rng.randint(0, 2**20))
            for index in range(count)
        ]
        evidence = topology_evidence(topology)
        assert evidence[0] == 1 and evidence[1] == count and len(evidence[2]) == 64
        checks += 3

        shuffled = list(topology)
        rng.shuffle(shuffled)
        assert topology_evidence(shuffled) == evidence
        checks += 1

        if topology:
            index = rng.randrange(len(topology))
            name, size = topology[index]
            resized = list(topology)
            resized[index] = (name, size + 1)
            assert topology_evidence(resized) != evidence
            checks += 1

            renamed = list(topology)
            renamed[index] = (name[:-6] + "-renamed:$DATA", size)
            assert topology_evidence(renamed) != evidence
            checks += 1

            # Content is intentionally absent from topology_evidence. Different
            # bytes of the same length must not affect names/sizes evidence.
            content_a = bytes(rng.getrandbits(8) for _ in range(min(size, 64)))
            content_b = bytes((byte ^ 0x5A) for byte in content_a)
            assert len(content_a) == len(content_b)
            assert topology_evidence(topology) == evidence
            checks += 2
        else:
            assert topology_evidence([]) == evidence
            checks += 1

    return checks


def run_sqlite_model(cases: int) -> int:
    rng = random.Random(20260810)
    connection = sqlite3.connect(":memory:")
    connection.execute("PRAGMA foreign_keys = ON")
    connection.executescript(
        """
        CREATE TABLE entries(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            state INTEGER NOT NULL,
            destination_identity TEXT NULL,
            PRIMARY KEY(operation_id, ordinal)
        ) WITHOUT ROWID;
        CREATE TABLE fingerprints(operation_id TEXT, ordinal INTEGER, digest TEXT, PRIMARY KEY(operation_id, ordinal));
        CREATE TABLE hard_links(operation_id TEXT, ordinal INTEGER, count INTEGER, PRIMARY KEY(operation_id, ordinal));
        CREATE TABLE metadata(operation_id TEXT, ordinal INTEGER, attributes INTEGER, PRIMARY KEY(operation_id, ordinal));
        CREATE TABLE security(operation_id TEXT, ordinal INTEGER, digest TEXT, PRIMARY KEY(operation_id, ordinal));
        CREATE TABLE streams(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            format_version INTEGER NOT NULL CHECK(format_version = 1),
            named_stream_count INTEGER NOT NULL CHECK(named_stream_count BETWEEN 0 AND 4096),
            digest TEXT NOT NULL CHECK(length(digest) = 64),
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal) REFERENCES entries(operation_id, ordinal)
        ) WITHOUT ROWID;
        """
    )
    checks = 0
    for index in range(cases):
        op = "op-{}".format(index)
        topology = [
            (random_stream_name(rng, stream_index), rng.randint(0, 2**20))
            for stream_index in range(rng.randint(0, 6))
        ]
        version, count, digest = topology_evidence(topology)
        connection.execute("INSERT INTO entries VALUES (?, 0, 1, NULL)", (op,))
        connection.commit()
        connection.execute("BEGIN IMMEDIATE")
        connection.execute("UPDATE entries SET state=2, destination_identity='id' WHERE operation_id=?", (op,))
        connection.execute("INSERT INTO fingerprints VALUES (?, 0, ?)", (op, hashlib.sha256(op.encode()).hexdigest()))
        connection.execute("INSERT INTO hard_links VALUES (?, 0, 1)", (op,))
        connection.execute("INSERT INTO metadata VALUES (?, 0, 32)", (op,))
        connection.execute("INSERT INTO security VALUES (?, 0, ?)", (op, hashlib.sha256((op + 'security').encode()).hexdigest()))
        connection.execute("INSERT INTO streams VALUES (?, 0, ?, ?, ?)", (op, version, count, digest))
        connection.commit()
        row = connection.execute(
            "SELECT e.state, s.format_version, s.named_stream_count, s.digest FROM entries e JOIN streams s USING(operation_id, ordinal) WHERE e.operation_id=?",
            (op,),
        ).fetchone()
        assert row == (2, 1, count, digest)
        checks += 4

    connection.execute("INSERT INTO entries VALUES ('rollback', 0, 1, NULL)")
    connection.commit()
    connection.executescript(
        """
        CREATE TRIGGER fail_streams BEFORE INSERT ON streams
        WHEN NEW.operation_id='rollback'
        BEGIN SELECT RAISE(ABORT, 'fixture'); END;
        """
    )
    try:
        connection.execute("BEGIN IMMEDIATE")
        connection.execute("UPDATE entries SET state=2, destination_identity='id' WHERE operation_id='rollback'")
        connection.execute("INSERT INTO fingerprints VALUES ('rollback', 0, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')")
        connection.execute("INSERT INTO hard_links VALUES ('rollback', 0, 1)")
        connection.execute("INSERT INTO metadata VALUES ('rollback', 0, 32)")
        connection.execute("INSERT INTO security VALUES ('rollback', 0, 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb')")
        connection.execute("INSERT INTO streams VALUES ('rollback', 0, 1, 0, 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc')")
        raise AssertionError("stream trigger should abort")
    except sqlite3.IntegrityError:
        connection.rollback()

    assert connection.execute("SELECT state, destination_identity FROM entries WHERE operation_id='rollback'").fetchone() == (1, None)
    for table in ("fingerprints", "hard_links", "metadata", "security", "streams"):
        assert connection.execute("SELECT 1 FROM {} WHERE operation_id='rollback'".format(table)).fetchone() is None
    checks += 6
    connection.close()
    return checks


def require(source: str, needles: Iterable[str], folded: bool = False) -> int:
    haystack = source.casefold() if folded else source
    checks = 0
    for needle in needles:
        candidate = needle.casefold() if folded else needle
        assert candidate in haystack, needle
        checks += 1
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "semantics": root / "src/FileOp.Core/Operations/FileOperationNamedDataStreamTopologyEvidence.cs",
        "contract": root / "src/FileOp.Core/Operations/FileCopyDestinationNamedDataStreamTopologyEvidence.cs",
        "verifier": root / "src/FileOp.Core/Operations/FileOperationRecoveryNamedDataStreamTopologyVerification.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore.cs",
        "digest": root / "src/FileOp.Windows/Operations/WindowsFileNamedDataStreamTopologyDigest.cs",
        "commit_source": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileCommitNamedDataStreamTopologyEvidenceSource.cs",
        "reader": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader.cs",
        "bridge": root / "src/FileOp.Windows/Operations/WindowsFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore.cs",
        "docs": root / "docs/file-operation-recovery-named-data-stream-topology-evidence.md",
        "tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistoryNamedDataStreamTopologyEvidenceTests.cs",
        "reader_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileNamedDataStreamTopologyEvidenceReaderTests.cs",
        "commit_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileCommitNamedDataStreamTopologyEvidenceSourceTests.cs",
        "bridge_tests": root / "tests/FileOp.Windows.Tests/WindowsFileOperationActionHistoryNamedDataStreamTopologyEvidenceStoreTests.cs",
        "verification_tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoveryNamedDataStreamTopologyVerificationTests.cs",
        "py_wrapper": root / "tools/test-copy-executor-local.py",
        "ps_wrapper": root / "tools/test-copy-executor-local.ps1",
        "windows_gate": root / "tools/test-windows-copy-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    checks += require(source["semantics"], (
        "CurrentFormatVersion = 1",
        "NamedStreamCount",
        "Sha256HexDigest",
        "SameNamesAndSizes",
        "DifferentNamesOrSizes",
        "NoRecordedEvidence",
        "Unavailable",
    ))
    checks += require(source["contract"], (
        "IFileCopyDestinationCommitNamedDataStreamTopologyEvidenceSource",
        "IFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore",
        "CommitCopyWithNamedDataStreamTopologyEvidenceAsync",
        "MarkMutationRecoveryRequiredWithNamedDataStreamTopologyEvidenceAsync",
        "GetDestinationNamedDataStreamTopologyEvidenceAsync",
    ))
    checks += require(source["verifier"], (
        "GetAsync(inspection.OperationId",
        "MatchesDurableHistory(",
        "GetDestinationNamedDataStreamTopologyEvidenceAsync(",
        "TryCreateRequest(",
        "IsConsistentSuccess(",
        "grants no mutation authority",
    ))
    checks += require(source["store"], (
        "file_operation_action_entry_named_data_stream_topology_evidence",
        "CHECK(format_version = 1)",
        "named_stream_count INTEGER NOT NULL CHECK(named_stream_count >= 0 AND named_stream_count <= 4096)",
        "file_operation_action_entry_content_fingerprints",
        "file_operation_action_entry_hard_link_evidence",
        "file_operation_action_entry_basic_metadata_evidence",
        "file_operation_action_entry_security_descriptor_evidence",
        "ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 5",
        "transaction.Commit();",
        "UndoKind.None",
    ), folded=True)
    for forbidden_column in ("stream_name", "stream_bytes", "stream_content"):
        assert forbidden_column not in source["store"].casefold(), forbidden_column
        checks += 1

    checks += require(source["digest"], (
        "FileStreamInfo = 7",
        "GetFileInformationByHandleEx(",
        '"::$DATA"',
        '":$DATA"',
        "StringComparer.Ordinal",
        "BinaryPrimitives.WriteInt64LittleEndian",
        "IncrementalHash.CreateHash(HashAlgorithmName.SHA256)",
        "MaximumBufferBytes",
        "MaximumNamedStreams",
    ))
    for forbidden in ("ReadFile(", "File.ReadAll", "FileStream", "FindFirstStreamW", "FindNextStreamW", "File.Delete(", "File.Move("):
        assert forbidden not in source["digest"], forbidden
        checks += 1

    for name in ("commit_source", "reader"):
        checks += require(source[name], (
            "RootDirectory = rootDirectory.DangerousGetHandle()",
            "NtCreateFile(",
            "WindowsFileNamedDataStreamTopologyDigest.Read(leafHandle)",
            "GetFileInformationByHandle(",
            "GetFinalPathNameByHandleW(",
        ))
        for forbidden in ("ReadFile(", "FileReadData", "File.Delete(", "File.Move(", "CanDelete", "CanUndo"):
            assert forbidden not in source[name], forbidden
            checks += 1

    checks += require(source["bridge"], (
        "ReadVerifiedEvidenceAsync(",
        "CommitCopyWithNamedDataStreamTopologyEvidenceAsync(",
        "MarkMutationRecoveryRequiredWithNamedDataStreamTopologyEvidenceAsync(",
        "pending.DestinationIdentity != identity",
        "pending.DestinationContentFingerprint != fingerprint",
    ))

    for test_name in (
        "StrongCommitPersistsAllEvidenceAcrossReopen",
        "StrongRecoveryPersistsTopologyWithoutUndoAuthority",
        "TopologyInsertFailureRollsBackStateAndAllEarlierEvidenceRows",
    ):
        assert test_name in source["tests"], test_name
        checks += 1
    for test_name in (
        "ReaderReportsNoNamedStreamsForOrdinaryNtfsFile",
        "NamedStreamNameOrSizeChangesTopologyDigestButSameSizeContentDoesNot",
        "ReplacedRootWithSameFileMovedBackFailsClosed",
    ):
        assert test_name in source["reader_tests"], test_name
        checks += 1
    for test_name in (
        "SourceCapturesExistingEvidenceAndNamedStreamTopology",
        "SameSizeNamedStreamContentRewriteDoesNotChangeTopologyEvidence",
        "WrongDestinationIdentityAndReplacedRootFailClosed",
    ):
        assert test_name in source["commit_tests"], test_name
        checks += 1
    for test_name in (
        "LegacyCommitCallCollectsAndPersistsNamedStreamTopologyEvidence",
        "FailedStrongCommitReusesExactTopologyObservationForRecovery",
        "MismatchedRecoveryEvidenceDowngradesToNoPartialProof",
    ):
        assert test_name in source["bridge_tests"], test_name
        checks += 1
    for test_name in (
        "MatchingTopologyIsEvidenceOnlyWithoutUndoAuthority",
        "DifferentTopologyIsReportedSeparately",
        "MismatchedDurableHistorySkipsEvidenceAndReader",
    ):
        assert test_name in source["verification_tests"], test_name
        checks += 1

    checks += require(source["docs"], (
        "names and logical sizes",
        "does **not** mean named-stream contents are equal",
        "raw stream names are not persisted",
        "same leaf handle",
        "five evidence inserts",
        "no mutation authority",
        "explicit user authorization",
    ), folded=True)

    verifier_name = "verify_recovery_named_data_stream_topology.py"
    assert verifier_name in source["py_wrapper"]
    assert verifier_name in source["ps_wrapper"]
    for filter_name in (
        "FileNamedDataStreamTopologyEvidenceTests",
        "FileOperationActionHistoryNamedDataStreamTopologyEvidenceTests",
        "FileOperationRecoveryNamedDataStreamTopologyVerificationTests",
        "WindowsRootBoundFileNamedDataStreamTopologyEvidenceReaderTests",
        "WindowsRootBoundFileCommitNamedDataStreamTopologyEvidenceSourceTests",
        "WindowsFileOperationActionHistoryNamedDataStreamTopologyEvidenceStoreTests",
    ):
        assert filter_name in source["windows_gate"], filter_name
        checks += 1
    checks += 2
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=50000)
    parser.add_argument("--sqlite-cases", type=int, default=5000)
    args = parser.parse_args()
    if args.cases < 0 or args.sqlite_cases < 0:
        raise ValueError("case counts must be non-negative")

    model_checks = run_model(args.cases)
    sqlite_checks = run_sqlite_model(args.sqlite_cases)
    repository_checks = check_repository(args.repo_root.resolve())
    print(
        "PASS: named-data-stream topology/size evidence verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases, "
        f"{sqlite_checks:,} SQLite assertions across {args.sqlite_cases:,} cases plus rollback, "
        f"and {repository_checks:,} source/gate checks."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
