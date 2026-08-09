#!/usr/bin/env python3
"""Zero-Actions compatibility checks for FILE_STREAM_INFO default-stream aliases."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def classify_stream_name(name: str) -> str:
    if name == "" or name.casefold() == "::$data":
        return "default"
    if not name.startswith(":") or not name.casefold().endswith(":$data"):
        return "invalid"
    stream_name = name[1 : -len(":$DATA")]
    if not stream_name or ":" in stream_name:
        return "invalid"
    return "named"


def run_model(cases: int) -> int:
    rng = random.Random(20260809)
    checks = 0

    assert classify_stream_name("") == "default"
    assert classify_stream_name("::$DATA") == "default"
    assert classify_stream_name("::$data") == "default"
    assert classify_stream_name(":ads:$DATA") == "named"
    assert classify_stream_name(":nested:name:$DATA") == "invalid"
    checks += 5

    alphabet = "abcdefghijklmnopqrstuvwxyz0123456789_-"
    for index in range(cases):
        default_name = "" if rng.getrandbits(1) == 0 else "::$DATA"
        assert classify_stream_name(default_name) == "default"
        checks += 1

        token = "".join(rng.choice(alphabet) for _ in range(rng.randint(1, 24)))
        named = f":{token}-{index}:$DATA"
        assert classify_stream_name(named) == "named"
        checks += 1

        malformed = f":{token}:nested:$DATA"
        assert classify_stream_name(malformed) == "invalid"
        checks += 1

    return checks


def check_repository(root: Path) -> int:
    parser_path = root / "src/FileOp.Windows/Operations/WindowsFileNamedDataStreamTopologyDigest.cs"
    test_path = root / "tests/FileOp.Windows.Tests/FileNamedDataStreamTopologyEvidenceTests.cs"
    docs_path = root / "docs/file-operation-recovery-named-data-stream-topology-evidence.md"
    for path in (parser_path, test_path, docs_path):
        if not path.is_file():
            raise FileNotFoundError(path)

    parser = parser_path.read_text(encoding="utf-8")
    tests = test_path.read_text(encoding="utf-8")
    docs = docs_path.read_text(encoding="utf-8")
    checks = 0

    invalid_length_start = parser.index("if ((streamNameLength & 1u) != 0")
    invalid_length_end = parser.index("if (streamSize < 0)", invalid_length_start)
    invalid_length_block = parser[invalid_length_start:invalid_length_end]
    assert "streamNameLength == 0" not in invalid_length_block
    checks += 1

    for needle in (
        "var name = streamNameLength == 0",
        "? string.Empty",
        "if (name.Length == 0 ||",
        'string.Equals(name, "::$DATA", StringComparison.OrdinalIgnoreCase)',
        "if (sawDefaultDataStream)",
    ):
        assert needle in parser, needle
        checks += 1

    assert "ParserAcceptsSpecDefinedEmptyDefaultStreamName" in tests
    assert "new byte[headerBytes]" in tests
    checks += 2

    folded_docs = docs.casefold()
    assert "zero-length stream name" in folded_docs
    assert "::$data" in folded_docs
    checks += 2

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=100000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repository_checks = check_repository(args.repo_root.resolve())
    print(
        "PASS: FILE_STREAM_INFO default-stream aliases verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases "
        f"and {repository_checks:,} source/test/documentation checks."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
