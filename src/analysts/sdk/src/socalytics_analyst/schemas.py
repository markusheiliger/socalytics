"""Offline schema registry and validation over the packaged copy of the canonical contracts."""

from __future__ import annotations

import json
import re
from datetime import datetime
from functools import lru_cache
from importlib import resources
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT202012

from . import errors

CONTRACTS_PACKAGE = "socalytics_analyst"
CONTRACTS_ROOT = "contracts"
COMMON_ID = "https://socalytics.invalid/contracts/common/v1/common.schema.json"

# Document kind -> (folder under contracts/analysis, schema file stem, supported versions per research R5).
KINDS: dict[str, tuple[str, str, tuple[str, ...]]] = {
    "analyst-job": ("analyst-job", "analyst-job", ("1.0.0", "1.1.0")),
    "analyst-attempt": ("analyst-attempt", "analyst-attempt", ("1.0.0",)),
    "upstream-results": ("upstream-results", "upstream-results", ("1.0.0", "1.1.0")),
    "result-manifest": ("result-manifest", "result-manifest", ("1.0.0", "1.1.0")),
    "fact-artifact": ("fact-artifact", "fact-artifact", ("1.0.0",)),
}

_VERSION = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
_DATE_TIME = re.compile(
    r"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$"
)

_FORMATS = FormatChecker()


@_FORMATS.checks("date-time")
def _is_date_time(value: object) -> bool:
    if not isinstance(value, str) or not _DATE_TIME.match(value):
        return False
    try:
        datetime.fromisoformat(value.replace("z", "Z"))
    except ValueError:
        return False
    return True


def _read(relative: str) -> dict[str, Any]:
    path = resources.files(CONTRACTS_PACKAGE).joinpath(CONTRACTS_ROOT, *relative.split("/"))
    return json.loads(path.read_text(encoding="utf-8"))


def _version_key(version: str) -> tuple[int, int, int]:
    major, minor, patch = _VERSION.match(version).groups()  # type: ignore[union-attr]
    return int(major), int(minor), int(patch)


@lru_cache(maxsize=1)
def registry() -> Registry:
    """Registry keyed by `$id` with the common definitions and the current schema of every kind."""
    entries = [_read("common/v1/common.schema.json")]
    for folder, stem, _ in KINDS.values():
        entries.append(_read(f"analysis/{folder}/v1/{stem}.schema.json"))
    resources_ = [(entry["$id"], Resource.from_contents(entry, DRAFT202012)) for entry in entries]
    return Registry().with_resources(resources_)


@lru_cache(maxsize=None)
def _validator(kind: str, version: str) -> Draft202012Validator:
    folder, stem, _ = KINDS[kind]
    schema = _read(f"analysis/{folder}/v1/releases/{version}.schema.json")
    return Draft202012Validator(schema, registry=registry(), format_checker=_FORMATS)


def supported_versions(kind: str) -> tuple[str, ...]:
    return KINDS[kind][2]


def select_version(kind: str, document: object) -> str:
    """Return the supported release whose schema validates `document`, by its `contractVersion`.

    A document of another major version or of a newer minor version than the SDK supports is refused. A document
    that does not state a well-formed version is validated against the newest release to name what is wrong.
    """
    supported = sorted(supported_versions(kind), key=_version_key)
    stated = document.get("contractVersion") if isinstance(document, dict) else None
    if not isinstance(stated, str) or not _VERSION.match(stated):
        return supported[-1]

    major, minor, _ = _version_key(stated)
    newest = _version_key(supported[-1])
    if major != 1 or minor > newest[1]:
        raise errors.InputError(
            errors.UNSUPPORTED_CONTRACT_VERSION,
            f"{kind} contract version {stated} is not supported; supported versions are {', '.join(supported)}.",
        )
    candidates = [version for version in supported if _version_key(version)[1] <= minor]
    return candidates[-1]


def _describe(error: Any) -> str:
    path = "/" + "/".join(str(part) for part in error.absolute_path)
    return f"{error.validator} at {path}"


def validate(kind: str, document: object, *, output: bool = False) -> str:
    """Validate `document` against its canonical schema and return the schema version used.

    Raises `InputError` (`input-invalid`, `unsupported-contract-version`) for read documents and `ResultError`
    (`output-invalid`) when `output` is true. Messages name the keyword and the path, never a document value.
    """
    version = supported_versions(kind)[-1] if output else select_version(kind, document)
    found = sorted(_validator(kind, version).iter_errors(document), key=lambda e: list(map(str, e.absolute_path)))
    if found:
        details = "; ".join(_describe(error) for error in found[:10])
        suffix = f" (and {len(found) - 10} more)" if len(found) > 10 else ""
        message = f"{kind} violates its schema {version}: {details}{suffix}."
        if output:
            raise errors.ResultError(errors.OUTPUT_INVALID, message)
        raise errors.InputError(errors.INPUT_INVALID, message)
    return version
