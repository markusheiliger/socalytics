"""Refresh or verify the SDK's copy of the contract schemas it implements.

Run from anywhere: ``python src/analysts/sdk/scripts/sync_contracts.py`` copies the schemas,
``--check`` only reports differences and exits non-zero when the copy is stale.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

SDK_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = SDK_ROOT.parents[2]
CANONICAL_ROOT = REPOSITORY_ROOT / "contracts"
COPY_ROOT = SDK_ROOT / "src" / "socalytics_analyst" / "contracts"

ANALYSIS_CONTRACTS = (
    "analyst-job",
    "analyst-attempt",
    "result-manifest",
    "upstream-results",
    "fact-artifact",
)

REFRESH_COMMAND = "python src/analysts/sdk/scripts/sync_contracts.py"


def canonical_files() -> list[Path]:
    """Return the canonical files to copy as paths relative to the contracts root."""
    files = [Path("common/v1/common.schema.json")]
    for name in ANALYSIS_CONTRACTS:
        version_dir = Path("analysis") / name / "v1"
        files.append(version_dir / f"{name}.schema.json")
        releases = CANONICAL_ROOT / version_dir / "releases"
        if releases.is_dir():
            files.extend(
                version_dir / "releases" / path.name
                for path in sorted(releases.glob("*.schema.json"))
            )
    return files


def copied_files() -> list[Path]:
    if not COPY_ROOT.is_dir():
        return []
    return sorted(
        path.relative_to(COPY_ROOT) for path in COPY_ROOT.rglob("*") if path.is_file()
    )


def find_differences() -> list[str]:
    """Describe every missing, differing, or stale file of the copy."""
    differences: list[str] = []
    expected = canonical_files()
    for relative in expected:
        source = CANONICAL_ROOT / relative
        target = COPY_ROOT / relative
        if not source.is_file():
            differences.append(f"canonical file is missing: contracts/{relative.as_posix()}")
        elif not target.is_file():
            differences.append(f"missing from the copy: {relative.as_posix()}")
        elif source.read_bytes() != target.read_bytes():
            differences.append(f"differs from the canonical file: {relative.as_posix()}")
    expected_set = set(expected)
    for relative in copied_files():
        if relative not in expected_set:
            differences.append(
                f"not a canonical file of an implemented contract: {relative.as_posix()}"
            )
    return differences


def sync() -> int:
    expected = canonical_files()
    for relative in expected:
        if not (CANONICAL_ROOT / relative).is_file():
            print(f"canonical file is missing: contracts/{relative.as_posix()}", file=sys.stderr)
            return 1
    expected_set = set(expected)
    for relative in copied_files():
        if relative not in expected_set:
            (COPY_ROOT / relative).unlink()
    for relative in expected:
        target = COPY_ROOT / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes((CANONICAL_ROOT / relative).read_bytes())
    print(f"Copied {len(expected)} schema files to {COPY_ROOT.as_posix()}")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check",
        action="store_true",
        help="report differences between the copy and the canonical contracts and exit non-zero",
    )
    arguments = parser.parse_args(argv)
    if not arguments.check:
        return sync()
    differences = find_differences()
    if not differences:
        return 0
    for difference in differences:
        print(difference, file=sys.stderr)
    print(f"Refresh the copy with: {REFRESH_COMMAND}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
