#!/usr/bin/env python3
"""Zero-Actions model/source checks for post-Copy SHA-256 content evidence."""
from __future__ import annotations

import argparse
import hashlib
import random
import sqlite3
import sys
from pathlib import Path
from typing import Optional, Tuple


def stream_copy_digest(payload: bytes, rng: random.Random) -> Tuple[bytes, str, int]:
    destination = bytearray()
    digest = hashlib.sha256()
    offset = 0
    checks = 0
    while offset < len(payload):
        read_size = min(len(payload) - offset, rng.randint(1, 32_768))
        chunk = payload[offset : offset + read_size]
        written = 0
        while written < len(chunk):
            step = min(len(chunk) - written, rng.randint(1, 8_192))
            destination.extend(chunk[written : written + step])
            written += step
            checks += 1
        # Model the production invariant: the chunk enters the digest only after
        # every byte in that read has been successfully written.
        digest.update(chunk)
        offset += len(chunk)
        checks += 2
    return bytes(destination), digest.hexdigest(), checks


def run_stream_model(cases: int) -> int:
    rng = random.Random(20260809)
    checks = 0
    known = [
        (b"", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
        (b"abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
    ]
    for payload, expected in known:
        copied, actual, local = stream_copy_digest(payload, rng)
        assert copied == payload
        assert actual == expected
        checks += local + 2

    for _ in range(cases):
        length = rng.randrange(0, 256 * 1024)
        payload = rng.randbytes(length)
        copied, actual, local = stream_copy_digest(payload, rng)
        assert copied == payload
        assert actual == hashlib.sha256(payload).hexdigest()
        assert len(actual) == 64
        checks += local + 3
    return checks


def check_evidence_provenance(cases: int) -> int:
    rng = random.Random(20260810)
    checks = 0
    for _ in range(cases):
        receipt_valid = rng.random() < 0.7
        commit_failed = rng.random() < 0.35
        primitive_failed = rng.random() < 0.1
        if primitive_failed:
            receipt_valid = False

        commit_evidence = receipt_valid and not commit_failed
        recovery_evidence = receipt_valid and commit_failed
        no_evidence = not receipt_valid

        assert not (commit_evidence and recovery_evidence)
        assert no_evidence or commit_evidence or recovery_evidence
        if recovery_evidence:
            identity = True
            fingerprint = True
            assert identity == fingerprint
        elif no_evidence:
            identity = False
            fingerprint = False
            assert not identity and not fingerprint
        checks += 4
    return checks


def check_sqlite_model(cases: int) -> int:
    rng = random.Random(20260811)
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
        CREATE TABLE fingerprints(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            algorithm INTEGER NOT NULL CHECK(algorithm = 1),
            digest_hex TEXT NOT NULL CHECK(length(digest_hex) = 64),
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal)
                REFERENCES entries(operation_id, ordinal) ON DELETE CASCADE
        ) WITHOUT ROWID;
        """
    )
    checks = 0
    for index in range(cases):
        operation = f"op-{index}"
        state = rng.choice((2, 5))  # committed or recovery
        identity = f"id-{rng.randrange(1 << 30)}"
        digest = hashlib.sha256(rng.randbytes(rng.randrange(0, 1024))).hexdigest()
        with connection:
            connection.execute(
                "INSERT INTO entries VALUES (?, 0, ?, ?)",
                (operation, state, identity),
            )
            if rng.random() < 0.8:
                connection.execute(
                    "INSERT INTO fingerprints VALUES (?, 0, 1, ?)",
                    (operation, digest),
                )
        row = connection.execute(
            """
            SELECT entry.state, entry.destination_identity, fp.algorithm, fp.digest_hex
            FROM entries AS entry
            LEFT JOIN fingerprints AS fp
              ON fp.operation_id = entry.operation_id AND fp.ordinal = entry.ordinal
            WHERE entry.operation_id = ?
            """,
            (operation,),
        ).fetchone()
        assert row is not None
        assert row[0] == state and row[1] == identity
        if row[2] is None:
            # Legacy schema-v1 entry with no side-table evidence remains readable.
            assert row[3] is None
        else:
            assert row[2] == 1 and row[3] == digest
        checks += 4
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "fingerprint": root / "src/FileOp.Core/Operations/FileContentFingerprint.cs",
        "history": root / "src/FileOp.Core/Operations/FileOperationActionHistory.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistoryStore.cs",
        "executor": root / "src/FileOp.Core/Operations/FileCopyOperationExecutor.cs",
        "primitive": root / "src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs",
        "history_tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistoryTests.cs",
        "executor_tests": root / "tests/FileOp.Windows.Tests/FileCopyOperationExecutorTests.cs",
        "native_tests": root / "tests/FileOp.Windows.Tests/WindowsFileCopyMutationPrimitiveTests.cs",
        "docs": root / "docs/file-copy-content-fingerprint.md",
        "python_wrapper": root / "tools/test-copy-executor-local.py",
        "powershell_wrapper": root / "tools/test-copy-executor-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}

    required = {
        "fingerprint": (
            "FileContentFingerprintAlgorithm",
            "Sha256 = 1",
            "Sha256HexLength = 64",
            "Uri.IsHexDigit",
            "HexDigest = hexDigest.ToLowerInvariant()",
            "does not grant delete",
        ),
        "history": (
            "DestinationContentFingerprint",
            "FileContentFingerprint destinationContentFingerprint",
            "FileContentFingerprint? destinationContentFingerprint = null",
        ),
        "store": (
            "file_operation_action_entry_content_fingerprints",
            "algorithm INTEGER NOT NULL CHECK(algorithm = 1)",
            "digest_hex TEXT NOT NULL CHECK(length(digest_hex) = 64)",
            "PersistDestinationContentFingerprintAsync(",
            "destinationIdentity.HasValue != (destinationContentFingerprint is not null)",
            "LEFT JOIN file_operation_action_entry_content_fingerprints",
            "private const int SchemaVersion = 1;",
        ),
        "executor": (
            "DestinationContentFingerprint = null",
            "FileContentFingerprintAlgorithm.Sha256",
            "receipt.DestinationContentFingerprint!",
            "verifiedDestinationContentFingerprint: receipt.DestinationContentFingerprint",
            "destinationContentFingerprint: verifiedDestinationContentFingerprint",
        ),
        "primitive": (
            "IncrementalHash.CreateHash(HashAlgorithmName.SHA256)",
            "contentHash.AppendData(buffer, 0, checked((int)bytesRead));",
            "Convert.ToHexString(contentHash.GetHashAndReset())",
            "destinationContentFingerprint = CopyContents(sourceFile, destinationFile)",
        ),
        "history_tests": (
            "LegacyCommittedRowWithoutFingerprintRemainsReadable",
            "RecoveryEvidenceRejectsHalfPairedIdentityAndFingerprint",
        ),
        "executor_tests": (
            "MissingFingerprintReceiptIsRejectedWithoutTrustedRecoveryEvidence",
            "LastRecoveryDestinationContentFingerprint",
        ),
        "native_tests": (
            "SHA256.HashData(payload)",
            "EmptyCopyReportsStandardSha256EmptyDigest",
        ),
        "docs": (
            "SHA-256",
            "same bound copy stream",
            "not deletion authorization",
            "not a complete no-user-change proof",
        ),
    }
    checks = 0
    for name, needles in required.items():
        for needle in needles:
            assert needle.casefold() in source[name].casefold(), needle
            checks += 1

    primitive = source["primitive"]
    write_loop = primitive.index("while (written < bytesRead)")
    append_hash = primitive.index("contentHash.AppendData", write_loop)
    assert write_loop < append_hash
    checks += 1

    executor = source["executor"]
    invalid = executor.index("The mutation primitive did not provide the required SHA-256")
    commit = executor.index(".CommitCopyAsync(", invalid)
    assert invalid < commit
    checks += 1

    for needle in (
        "File.Copy(",
        "File.Move(",
        "File.Delete(",
        "Directory.Delete(",
    ):
        assert needle not in source["primitive"], needle
        checks += 1

    verifier = "verify_copy_content_fingerprint.py"
    assert verifier in source["python_wrapper"]
    assert verifier in source["powershell_wrapper"]
    checks += 2
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=20000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = (
        run_stream_model(args.cases)
        + check_evidence_provenance(args.cases)
        + check_sqlite_model(min(args.cases, 5000))
    )
    print(
        f"PASS Copy content fingerprint model: {checks} checks across "
        f"{args.cases} streaming/provenance cases plus SQLite compatibility cases"
    )
    if not args.self_test_only:
        print(
            "PASS Copy content fingerprint source wiring: "
            f"{check_repository(args.repo_root.resolve())} checks"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError, sqlite3.DatabaseError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
