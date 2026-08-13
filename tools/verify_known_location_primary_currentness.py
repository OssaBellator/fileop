#!/usr/bin/env python3
"""Verify primary currentness for known-location evidence without .NET or Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def primary_state_current(mode: str, busy: bool, current: bool) -> bool:
    return mode == "Native" and not busy and current


def source_is_usable(
    *,
    mode: str,
    busy: bool,
    current: bool,
    identity_matches: bool,
    root_matches: bool,
    checkpoint: bool,
) -> bool:
    return (
        primary_state_current(mode, busy, current)
        and identity_matches
        and root_matches
        and checkpoint
    )


def run_model(cases: int, seed: int) -> int:
    checks = 0
    assert source_is_usable(
        mode="Native", busy=False, current=True,
        identity_matches=True, root_matches=True, checkpoint=True,
    )
    assert not source_is_usable(
        mode="Native", busy=False, current=False,
        identity_matches=True, root_matches=True, checkpoint=True,
    )
    assert not source_is_usable(
        mode="Native", busy=True, current=True,
        identity_matches=True, root_matches=True, checkpoint=True,
    )
    assert not source_is_usable(
        mode="Fallback", busy=False, current=True,
        identity_matches=True, root_matches=True, checkpoint=True,
    )
    checks += 4

    rng = random.Random(seed)
    modes = ("Native", "Fallback", "Initializing", "Unavailable")
    for _ in range(cases):
        mode = rng.choice(modes)
        busy = rng.random() < 0.15
        current = rng.random() < 0.75
        identity_matches = rng.random() < 0.90
        root_matches = rng.random() < 0.92
        checkpoint = rng.random() < 0.88
        result = source_is_usable(
            mode=mode,
            busy=busy,
            current=current,
            identity_matches=identity_matches,
            root_matches=root_matches,
            checkpoint=checkpoint,
        )
        expected = (
            mode == "Native"
            and not busy
            and current
            and identity_matches
            and root_matches
            and checkpoint
        )
        assert result == expected
        assert not result or checkpoint
        assert not result or primary_state_current(mode, busy, current)
        checks += 3
    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    source = (
        root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewSource.cs"
    ).read_text(encoding="utf-8")
    protocol = (
        root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs"
    ).read_text(encoding="utf-8")
    checks = 0

    for needle, label in (
        ("private bool IsPrimaryReviewStateCurrent() =>", "primary currentness helper"),
        ("Mode: DesktopSearchMode.Native", "native-mode requirement"),
        ("IsBusy: false", "non-busy requirement"),
        ("IsCurrent: true", "journal-current requirement"),
        ("currentDescriptor.HasCheckpoint", "catalog checkpoint requirement"),
    ):
        checks += require(source, needle, label)

    first_state = source.index("!IsPrimaryReviewStateCurrent()")
    search_gate = source.index("await _searchOperationGate.WaitAsync(token)", first_state)
    second_state = source.index("!IsPrimaryReviewStateCurrent()", search_gate)
    native_gate = source.index("await _nativeOperationGate.WaitAsync(token)", second_state)
    third_state = source.index("!IsPrimaryReviewStateCurrent()", native_gate)
    catalog_read = source.index("GetVolumesAsync(token)", third_state)
    if not first_state < search_gate < second_state < native_gate < third_state < catalog_read:
        raise AssertionError(
            "primary currentness must be rechecked around both gates before catalog freshness"
        )
    checks += 1

    checks += require(protocol, "public const int CurrentVersion = 8;", "protocol v8")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xC011EC7)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        "PASS: known-location primary currentness verifier: "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
