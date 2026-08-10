#!/usr/bin/env python3
"""Zero-Actions compatibility checks for FILE_STREAM_INFO default-stream aliases."""
from __future__ import annotations

import argparse
import random
import struct
from pathlib import Path

HEADER_BYTES = 24
INT_MAX = 2**31 - 1
MAX_NAMED_STREAMS = 4096


def classify_stream_name(name: str) -> str:
    if name == "" or name.casefold() == "::$data":
        return "default"
    if not name.startswith(":") or not name.casefold().endswith(":$data"):
        return "invalid"
    stream_name = name[1 : -len(":$DATA")]
    if not stream_name or ":" in stream_name:
        return "invalid"
    return "named"


def encode_inventory(entries: list[tuple[str, int]]) -> bytes:
    chunks: list[bytearray] = []
    for index, (name, size) in enumerate(entries):
        encoded_name = name.encode("utf-16le")
        entry = bytearray(HEADER_BYTES + len(encoded_name))
        struct.pack_into("<IIqq", entry, 0, 0, len(encoded_name), size, size)
        entry[HEADER_BYTES:] = encoded_name
        if index != len(entries) - 1:
            padded_length = (len(entry) + 7) & ~7
            entry.extend(b"\0" * (padded_length - len(entry)))
            struct.pack_into("<I", entry, 0, padded_length)
        chunks.append(entry)
    return b"".join(chunks)


def parse_inventory(buffer: bytes) -> list[tuple[str, int]]:
    result: list[tuple[str, int]] = []
    seen: set[str] = set()
    offset = 0
    entries_seen = 0
    saw_default_data_stream = False

    while True:
        entries_seen += 1
        if entries_seen > MAX_NAMED_STREAMS + 1:
            raise ValueError("implausibly many entries")
        if offset < 0 or offset > len(buffer) - HEADER_BYTES:
            raise ValueError("entry offset outside buffer")

        next_entry_offset, stream_name_length, stream_size, _ = struct.unpack_from(
            "<IIqq", buffer, offset
        )
        if (
            (stream_name_length & 1) != 0
            or stream_name_length > INT_MAX
            or stream_name_length > len(buffer) - offset - HEADER_BYTES
        ):
            raise ValueError("invalid UTF-16 stream-name length")
        if stream_size < 0:
            raise ValueError("negative logical stream size")

        raw_name = buffer[
            offset + HEADER_BYTES : offset + HEADER_BYTES + stream_name_length
        ]
        try:
            name = raw_name.decode("utf-16le", errors="strict")
        except UnicodeDecodeError as exception:
            raise ValueError("invalid UTF-16 stream name") from exception
        if "\0" in name:
            raise ValueError("embedded NUL in stream name")

        classification = classify_stream_name(name)
        if classification == "default":
            if saw_default_data_stream:
                raise ValueError("default stream returned more than once")
            saw_default_data_stream = True
        elif classification == "named":
            if name in seen:
                raise ValueError("duplicate named stream")
            seen.add(name)
            result.append((name, stream_size))
            if len(result) > MAX_NAMED_STREAMS:
                raise ValueError("named stream count exceeded")
        else:
            raise ValueError("unexpected non-$DATA stream entry")

        if next_entry_offset == 0:
            break
        if (
            (next_entry_offset & 7) != 0
            or next_entry_offset < HEADER_BYTES + stream_name_length
            or next_entry_offset > INT_MAX
            or next_entry_offset > len(buffer) - offset
        ):
            raise ValueError("invalid next-entry offset")
        offset += next_entry_offset

    if not saw_default_data_stream:
        raise ValueError("missing unnamed default data stream")
    return sorted(result, key=lambda entry: entry[0])


def expect_invalid(buffer: bytes) -> None:
    try:
        parse_inventory(buffer)
    except ValueError:
        return
    raise AssertionError("malformed FILE_STREAM_INFO inventory was accepted")


def run_model(cases: int) -> int:
    rng = random.Random(20260809)
    checks = 0

    assert classify_stream_name("") == "default"
    assert classify_stream_name("::$DATA") == "default"
    assert classify_stream_name("::$data") == "default"
    assert classify_stream_name(":ads:$DATA") == "named"
    assert classify_stream_name(":nested:name:$DATA") == "invalid"
    checks += 5

    assert parse_inventory(encode_inventory([("", 0)])) == []
    assert parse_inventory(encode_inventory([("::$DATA", 0)])) == []
    assert parse_inventory(encode_inventory([("", 0), (":ads:$DATA", 4)])) == [
        (":ads:$DATA", 4)
    ]
    expect_invalid(encode_inventory([("", 0), ("::$DATA", 0)]))
    expect_invalid(encode_inventory([(":ads:$DATA", 1)]))
    expect_invalid(encode_inventory([("", 0), (":ads:$INDEX_ALLOCATION", 1)]))
    expect_invalid(
        encode_inventory([("", 0), (":ads:$DATA", 1), (":ads:$DATA", 2)])
    )
    checks += 7

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

        named_entries: list[tuple[str, int]] = []
        for stream_index in range(rng.randint(0, 8)):
            stream_token = "".join(
                rng.choice(alphabet) for _ in range(rng.randint(1, 24))
            )
            named_entries.append(
                (
                    f":{stream_token}-{index}-{stream_index}:$DATA",
                    rng.randint(0, 2**20),
                )
            )
        expected = sorted(named_entries, key=lambda entry: entry[0])
        rng.shuffle(named_entries)
        insert_at = rng.randint(0, len(named_entries))
        inventory = (
            named_entries[:insert_at]
            + [(default_name, rng.randint(0, 2**20))]
            + named_entries[insert_at:]
        )
        assert parse_inventory(encode_inventory(inventory)) == expected
        checks += 1

        mode = index % 6
        if mode == 0:
            malformed_buffer = bytearray(encode_inventory(inventory))
            struct.pack_into("<I", malformed_buffer, 4, 1)
        elif mode == 1:
            malformed_buffer = bytearray(encode_inventory(inventory))
            struct.pack_into("<q", malformed_buffer, 8, -1)
        elif mode == 2:
            malformed_buffer = bytearray(encode_inventory([(":only-named:$DATA", 1)]))
        elif mode == 3:
            if len(inventory) > 1:
                malformed_buffer = bytearray(encode_inventory(inventory))
                struct.pack_into("<I", malformed_buffer, 0, 25)
            else:
                malformed_buffer = bytearray(
                    encode_inventory([("", 0), ("::$DATA", 0)])
                )
        elif mode == 4:
            malformed_buffer = bytearray(
                encode_inventory([("", 0), (":dup:$DATA", 1), (":dup:$DATA", 2)])
            )
        else:
            malformed_buffer = bytearray(
                encode_inventory([("", 0), (":ads:$INDEX_ALLOCATION", 1)])
            )
        expect_invalid(bytes(malformed_buffer))
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
        f"{model_checks:,} classifier/parser-model assertions across {args.cases:,} randomized cases "
        f"and {repository_checks:,} source/test/documentation checks."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
