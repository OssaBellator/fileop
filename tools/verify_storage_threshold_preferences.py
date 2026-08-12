#!/usr/bin/env python3
"""Verify persisted policy-relative Optimize display-threshold preferences."""
from __future__ import annotations

import argparse
import json
import random
from pathlib import Path

LONG_MAX = (1 << 63) - 1
INT_MAX = (1 << 31) - 1
SIZE_MULTIPLIERS = (1, 2, 4, 8)
AGE_MULTIPLIERS = (1, 2, 4, 6)
MAX_PREFERENCE_BYTES = 4_096


def sat_mul(value: int, multiplier: int, maximum: int) -> int:
    return maximum if value > maximum // multiplier else value * multiplier


def supported(preference: tuple[int, int, int]) -> bool:
    large, same, stale = preference
    return large in SIZE_MULTIPLIERS and same in SIZE_MULTIPLIERS and stale in AGE_MULTIPLIERS


def resolve(
    policy: tuple[int, int, int],
    preference: tuple[int, int, int] | None,
) -> tuple[int, int, int]:
    effective = preference if preference is not None and supported(preference) else (1, 1, 1)
    return (
        sat_mul(policy[0], effective[0], LONG_MAX),
        sat_mul(policy[1], effective[1], LONG_MAX),
        sat_mul(policy[2], effective[2], INT_MAX),
    )


def parse_preference(data: bytes) -> tuple[int, int, int] | None:
    if not data or len(data) > MAX_PREFERENCE_BYTES:
        return None

    try:
        pairs = json.loads(data.decode("utf-8"), object_pairs_hook=lambda value: value)
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None
    if not isinstance(pairs, list):
        return None

    names: set[str] = set()
    values: dict[str, int] = {}
    for item in pairs:
        if not isinstance(item, tuple) or len(item) != 2:
            return None
        name, value = item
        if not isinstance(name, str) or name in names:
            return None
        if type(value) is not int:
            return None
        names.add(name)
        values[name] = value

    expected = {
        "SchemaVersion",
        "LargeFileMultiplier",
        "SameSizeMultiplier",
        "StaleAgeMultiplier",
    }
    if names != expected or values.get("SchemaVersion") != 1:
        return None
    preference = (
        values["LargeFileMultiplier"],
        values["SameSizeMultiplier"],
        values["StaleAgeMultiplier"],
    )
    return preference if supported(preference) else None


def encode_preference(preference: tuple[int, int, int]) -> bytes:
    if not supported(preference):
        raise ValueError("unsupported preference")
    return (
        json.dumps(
            {
                "SchemaVersion": 1,
                "LargeFileMultiplier": preference[0],
                "SameSizeMultiplier": preference[1],
                "StaleAgeMultiplier": preference[2],
            },
            separators=(",", ":"),
        )
        + "\n"
    ).encode("utf-8")


def filter_model(
    policy: tuple[int, int, int],
    preference: tuple[int, int, int],
    files: list[int],
    groups: list[int],
    ages: list[int],
) -> tuple[list[int], list[int], list[int]]:
    large, same, stale = resolve(policy, preference)
    return (
        [value for value in files if value >= large],
        [value for value in groups if value >= same],
        [age for age in ages if age >= stale],
    )


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    baseline_policy = (100, 50, 10)
    preference = (4, 2, 6)
    assert resolve(baseline_policy, preference) == (400, 100, 60)
    assert resolve(baseline_policy, (3, 2, 2)) == baseline_policy
    assert parse_preference(encode_preference(preference)) == preference
    checks += 3

    # Numeric saturation can make distinct semantic multipliers resolve to the same
    # displayed threshold. The persisted preference therefore must never be inferred
    # backwards from the numeric threshold alone.
    saturated_policy = (LONG_MAX // 2 + 1, LONG_MAX // 4 + 1, INT_MAX // 2 + 1)
    lower_preference = (2, 4, 2)
    higher_preference = (8, 8, 6)
    assert lower_preference != higher_preference
    assert resolve(saturated_policy, lower_preference) == resolve(saturated_policy, higher_preference)
    checks += 2

    invalid_documents = (
        b"not json",
        b"{}",
        b'{"SchemaVersion":2,"LargeFileMultiplier":2,"SameSizeMultiplier":2,"StaleAgeMultiplier":2}',
        b'{"SchemaVersion":1,"LargeFileMultiplier":3,"SameSizeMultiplier":2,"StaleAgeMultiplier":2}',
        b'{"SchemaVersion":1,"SchemaVersion":1,"LargeFileMultiplier":2,"SameSizeMultiplier":2,"StaleAgeMultiplier":2}',
        b'{"SchemaVersion":1,"LargeFileMultiplier":2,"SameSizeMultiplier":2,"StaleAgeMultiplier":2,"Extra":1}',
        b"x" * (MAX_PREFERENCE_BYTES + 1),
    )
    for document in invalid_documents:
        assert parse_preference(document) is None
        checks += 1

    for _ in range(cases):
        large_policy = rng.randint(1, LONG_MAX)
        same_policy = rng.randint(1, LONG_MAX)
        stale_policy = rng.randint(1, INT_MAX)
        policy = (large_policy, same_policy, stale_policy)
        preference = (
            rng.choice(SIZE_MULTIPLIERS),
            rng.choice(SIZE_MULTIPLIERS),
            rng.choice(AGE_MULTIPLIERS),
        )
        thresholds = resolve(policy, preference)

        assert thresholds[0] == sat_mul(large_policy, preference[0], LONG_MAX)
        assert thresholds[1] == sat_mul(same_policy, preference[1], LONG_MAX)
        assert thresholds[2] == sat_mul(stale_policy, preference[2], INT_MAX)
        assert thresholds[0] >= large_policy
        assert thresholds[1] >= same_policy
        assert thresholds[2] >= stale_policy
        checks += 6

        fresh_policy = (
            rng.randint(1, LONG_MAX),
            rng.randint(1, LONG_MAX),
            rng.randint(1, INT_MAX),
        )
        fresh = resolve(fresh_policy, preference)
        assert fresh[0] == sat_mul(fresh_policy[0], preference[0], LONG_MAX)
        assert fresh[1] == sat_mul(fresh_policy[1], preference[1], LONG_MAX)
        assert fresh[2] == sat_mul(fresh_policy[2], preference[2], INT_MAX)
        assert all(current >= base for current, base in zip(fresh, fresh_policy))
        checks += 4

        encoded = encode_preference(preference)
        assert len(encoded) <= MAX_PREFERENCE_BYTES
        assert parse_preference(encoded) == preference
        checks += 2

        unsupported = (3, preference[1], preference[2])
        assert resolve(policy, unsupported) == policy
        checks += 1

        files = [rng.randint(large_policy, LONG_MAX) for _ in range(rng.randint(0, 20))]
        groups = [rng.randint(same_policy, LONG_MAX) for _ in range(rng.randint(0, 20))]
        ages = [rng.randint(stale_policy, INT_MAX) for _ in range(rng.randint(0, 20))]
        filtered = filter_model(policy, preference, files, groups, ages)
        assert filtered[0] == [value for value in files if value >= thresholds[0]]
        assert filtered[1] == [value for value in groups if value >= thresholds[1]]
        assert filtered[2] == [age for age in ages if age >= thresholds[2]]
        checks += 3

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    preference = (root / "src/FileOp.Core/Storage/StorageOptimizationThresholdPreference.cs").read_text(encoding="utf-8")
    store = (root / "src/FileOp.Core/Storage/JsonFileStorageOptimizationThresholdPreferenceStore.cs").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageOptimizationView.Thresholds.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/StorageOptimizationThresholdPreferenceTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/storage-threshold-preferences.md").read_text(encoding="utf-8")
    overlay_verifier = (root / "tools/verify_storage_threshold_overlay.py").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")

    checks = 0
    for text, needle, label in (
        (preference, "public sealed record StorageOptimizationThresholdPreference(", "typed multiplier preference"),
        (preference, "public static StorageOptimizationThresholdPreference Baseline", "baseline preference"),
        (preference, "public static IReadOnlyList<int> SupportedSizeMultipliers { get; } =\n        Array.AsReadOnly(new[] { 1, 2, 4, 8 });", "size multiplier allow-list"),
        (preference, "public static IReadOnlyList<int> SupportedStaleAgeMultipliers { get; } =\n        Array.AsReadOnly(new[] { 1, 2, 4, 6 });", "age multiplier allow-list"),
        (preference, "analysis.Policy.LargeFileMinimumBytes", "fresh large policy resolution"),
        (preference, "analysis.Policy.SameSizeMinimumBytes", "fresh same-size policy resolution"),
        (preference, "analysis.Policy.StaleAgeDays", "fresh age policy resolution"),
        (preference, "StorageOptimizationThresholdFilter.ValidateAgainstAnalysis(analysis, thresholds);", "same-or-stricter validation"),
        (preference, "long.MaxValue / multiplier", "long saturation"),
        (preference, "int.MaxValue / multiplier", "int saturation"),
        (store, "private const int SchemaVersion = 1;", "preference schema version"),
        (store, "private const long MaximumPreferenceBytes = 4_096;", "preference size bound"),
        (store, "Path.GetFullPath(path)", "canonical preference path"),
        (store, "JsonDocument.Parse(bytes)", "strict JSON parse"),
        (store, "if (!names.Add(property.Name)", "duplicate-field refusal"),
        (store, 'case "LargeFileMultiplier":', "large multiplier field"),
        (store, 'case "SameSizeMultiplier":', "same-size multiplier field"),
        (store, 'case "StaleAgeMultiplier":', "age multiplier field"),
        (store, "File.Move(temporaryPath, _path, overwrite: true);", "same-directory replacement"),
        (view, "Environment.SpecialFolder.LocalApplicationData", "per-user local application data"),
        (view, '"storage-optimization-thresholds.v1.json"', "versioned preference filename"),
        (view, "private sealed record SizeThresholdOption(long Value, int Multiplier, string Label);", "semantic size option"),
        (view, "private sealed record AgeThresholdOption(int Value, int Multiplier, string Label);", "semantic age option"),
        (view, "_thresholdPreference = new StorageOptimizationThresholdPreference(", "direct semantic preference construction"),
        (view, "StorageOptimizationThresholdPreferencePolicy.Resolve(\n                analysis,\n                _thresholdPreference);", "fresh-analysis preference resolution"),
        (view, "option.Multiplier == _thresholdPreference.LargeFileMultiplier", "large semantic selection"),
        (view, "option.Multiplier == _thresholdPreference.SameSizeMultiplier", "same-size semantic selection"),
        (view, "option.Multiplier == _thresholdPreference.StaleAgeMultiplier", "stale semantic selection"),
        (view, ".GroupBy(static option => option.Value)", "saturated numeric option collapse"),
        (view, "Interlocked.Increment(ref _thresholdPreferenceGeneration)", "user-change generation"),
        (view, "Volatile.Read(ref _thresholdPreferenceGeneration)", "late-load/save generation guard"),
        (view, "await _thresholdPreferenceSaveGate.WaitAsync();", "serialized preference saves"),
        (view, "_ = LoadThresholdPreferenceAsync();", "lazy preference load"),
        (view, "_ = SaveThresholdPreferenceAsync(_thresholdPreference, generation);", "selection preference save"),
        (view, "does not rerun the helper", "no-rerun UI boundary"),
        (tests, "SamePreferenceReResolvesAgainstFreshPolicy", "fresh policy regression"),
        (tests, "SaturatedThresholdsDoNotImplyAUniqueMultiplier", "saturated semantic regression"),
        (tests, "CorruptUnknownUnsupportedAndOversizedPreferencesFallBackToNoPreference", "invalid preference regression"),
        (tests, "PreferenceStoreRoundTripsAndLatestSaveWins", "store replacement regression"),
        (docs, "never persists raw byte or day thresholds", "raw threshold persistence boundary"),
        (docs, "does not implement helper-policy transport", "helper transport remains open"),
        (docs, "never reverse-infers a multiplier from a saturated numeric threshold", "saturation semantic boundary"),
        (overlay_verifier, "remembered as policy-relative multipliers", "existing overlay verifier updated for persistence"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 unchanged"),
        (gate, "verify_storage_threshold_preferences.py --repo-root $repoRoot --cases 50000", "offline preference gate"),
    ):
        checks += require(text, needle, label)

    for text, needle, label in (
        (preference, "FromThresholds(", "ambiguous numeric-to-semantic reverse inference"),
        (store, "LargeFileMinimumBytes", "raw large threshold persistence"),
        (store, "SameSizeMinimumBytes", "raw same-size threshold persistence"),
        (store, "StaleAgeDays", "raw stale-age persistence"),
        (store, "RootPath", "source path persistence"),
        (store, "Candidate", "candidate persistence"),
        (view, "AnalyzeStorageOptimizationAsync(", "helper rerun from threshold view"),
        (view, "IndexingStorageOptimizationRequest", "helper protocol request from threshold view"),
        (view, "SHA256", "hashing from threshold preference view"),
        (view, "PeriodicTimer", "threshold preference polling"),
        (view, "DispatcherQueueTimer", "threshold preference timer"),
        (protocol, "StorageOptimizationThresholdPreference", "preference protocol transport"),
    ):
        checks += forbid(text, needle, label)

    if gate.count("verify_storage_threshold_preferences.py") != 1:
        raise AssertionError("threshold preference verifier must be wired exactly once")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x7A125EED)
    args = parser.parse_args()
    if args.cases < 0:
        parser.error("--cases must be non-negative")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source/test checks" if args.repo_root else ""
    print(
        f"PASS: Optimize threshold preferences verified with {model_checks:,} model/file-format assertions "
        f"across {args.cases:,} randomized policy/preference states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
