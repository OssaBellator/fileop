#!/usr/bin/env python3
"""Targeted regressions for Storage UI cases that are easy to miss in broad property tests."""
from verify_storage_ui import _assert_exact_categories


def main() -> int:
    # A bounded physical-allocation response can display only one extension while an
    # exact category row for another extension legitimately carries zero physical
    # bytes because that namespace consists only of non-canonical hard-link aliases.
    _assert_exact_categories(
        [("Data", 128, 1), ("Images", 0, 1)],
        root_weight=128,
        complete_type_count=2,
        displayed_type_count=1,
    )

    print("PASS exact category rollup with zero-physical hard-link category")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
