#!/usr/bin/env python3
"""Verify Windows named-data-stream component rules without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

MAX_STREAM_NAME_CHARACTERS = 255


def classify_stream_name(name: str) -> str:
    if name == "" or name.casefold() == "::$data":
        return "default"
    if not name.startswith(":") or not name.casefold().endswith(":$data"):
        return "invalid"

    stream_name = name[1 : -len(":$DATA")]
    if not stream_name or len(stream_name) > MAX_STREAM_NAME_CHARACTERS:
        return "invalid"
    if any(character in stream_name for character in "\\/:"):
        return "invalid"
    if "\0" in stream_name:
        return "invalid"
    return "named"


def run_model(cases: int) -> int:
    rng = random.Random(20260810)
    alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-. $"
    checks = 0

    expected = {
        "": "default",
        "::$DATA": "default",
        ":ads:$DATA": "named",
        ":$DATA:$DATA": "named",
        f":{new_name(255, 'a')}:$DATA": "named",
        f":{new_name(256, 'a')}:$DATA": "invalid",
        ":parent/child:$DATA": "invalid",
        ":parent\\child:$DATA": "invalid",
        ":parent:child:$DATA": "invalid",
        ":bad\0name:$DATA": "invalid",
    }
    for name, classification in expected.items():
        assert classify_stream_name(name) == classification
        checks += 1

    for case in range(cases):
        length = rng.randint(1, MAX_STREAM_NAME_CHARACTERS)
        component = "".join(rng.choice(alphabet) for _ in range(length))
        assert classify_stream_name(f":{component}:$DATA") == "named"
        checks += 1

        separator = "\\/:"[case % 3]
        split_at = rng.randint(0, len(component))
        malformed = component[:split_at] + separator + component[split_at:]
        assert classify_stream_name(f":{malformed}:$DATA") == "invalid"
        checks += 1

        overlong = "x" * (MAX_STREAM_NAME_CHARACTERS + 1 + (case % 8))
        assert classify_stream_name(f":{overlong}:$DATA") == "invalid"
        checks += 1

    return checks


def new_name(length: int, character: str) -> str:
    return character * length


def check_repository(root: Path) -> int:
    parser_path = root / "src/FileOp.Windows/Operations/WindowsFileNamedDataStreamTopologyDigest.cs"
    test_path = root / "tests/FileOp.Windows.Tests/FileNamedDataStreamNameValidationTests.cs"
    for path in (parser_path, test_path):
        if not path.is_file():
            raise FileNotFoundError(path)

    parser = parser_path.read_text(encoding="utf-8")
    tests = test_path.read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "private const int MaximumStreamNameCharacters = 255;",
        "streamNameLength is <= 0 or > MaximumStreamNameCharacters",
        "streamName.IndexOf(':') < 0",
        "streamName.IndexOf('\\\\') < 0",
        "streamName.IndexOf('/') < 0",
    ):
        assert needle in parser, needle
        checks += 1

    for needle in (
        "NamedDataStreamValidatorEnforcesWindowsStreamNameRules",
        "new string('a', 255)",
        "new string('a', 256)",
        'IsValid(":parent/child:$DATA")',
        'IsValid(":parent\\\\child:$DATA")',
    ):
        assert needle in tests, needle
        checks += 1

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=100000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repository_checks = 0
    if args.repo_root is not None:
        repository_checks = check_repository(args.repo_root.resolve())

    print(
        "PASS: named-stream component rules verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases"
        + (
            f" and {repository_checks:,} source/test checks."
            if args.repo_root is not None
            else "."
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
