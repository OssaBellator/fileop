#!/usr/bin/env python3
"""Compatibility gate entry for machine-wide process and CPU activity verification."""
from verify_machine_process_activity import main
from verify_system_cpu_activity_ui import main as system_cpu_activity_ui_main


def combined_main() -> int:
    result = main()
    if result != 0:
        return result
    return system_cpu_activity_ui_main()


if __name__ == "__main__":
    raise SystemExit(combined_main())
