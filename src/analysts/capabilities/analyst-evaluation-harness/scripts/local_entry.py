"""Standalone launcher that runs an Analyst function against a staged mount root.

The SDK's `run` reads `/socalytics` by default and accepts another root only as an argument, so a local subject
command runs this script inside the capability's own environment:

    python local_entry.py module:function --output-type foundational-facts --root <mount>

It imports `module:function` and calls `socalytics_analyst.run(function, output_type=..., root=<mount>)`, which
exits with the SDK's status codes. The harness itself never imports the capability. The script uses only the
standard library and the SDK.
"""

from __future__ import annotations

import argparse
import importlib
import sys
from pathlib import Path

LAUNCH_EXIT_CODE = 1


def _fail(message: str) -> None:
    print(f"local-entry: {message}", file=sys.stderr)
    sys.exit(LAUNCH_EXIT_CODE)


def _load(target: str):
    module_name, separator, attribute_path = target.partition(":")
    if not separator or not module_name or not attribute_path:
        _fail("analyst must be given as module:function")
    try:
        function = importlib.import_module(module_name)
        for part in attribute_path.split("."):
            function = getattr(function, part)
    except (ImportError, AttributeError):
        _fail(f"cannot load analyst {target}")
    if not callable(function):
        _fail(f"analyst {target} is not callable")
    return function


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description="Run an Analyst function against a staged mount root.")
    parser.add_argument("analyst", help="module:function of the Analyst function")
    parser.add_argument("--output-type", required=True, help="output type passed to the SDK")
    parser.add_argument("--root", required=True, type=Path, help="mount root holding job.json and attempt.json")
    arguments = parser.parse_args(argv)

    analyst = _load(arguments.analyst)
    from socalytics_analyst import run

    run(analyst, output_type=arguments.output_type, root=arguments.root)


if __name__ == "__main__":
    main()
