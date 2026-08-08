#!/usr/bin/env python3
"""Targeted regressions for Storage UI cases that are easy to miss in broad property tests."""
from verify_storage_ui import _prepare_category_weights


def main() -> int:
    # A bounded physical-allocation response can omit extension groups whose only
    # names are non-canonical hard-link aliases. The response is still truncated,
    # but those omitted groups add no physical bytes and must not invent an Other bar.
    categories, omitted_count, omitted_weight = _prepare_category_weights(
        [("Data", 128)],
        root_weight=128,
        complete_type_count=2,
    )
    assert categories == {"Data": 128}
    assert omitted_count == 1
    assert omitted_weight == 0

    print("PASS category truncation with zero physical remainder")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
