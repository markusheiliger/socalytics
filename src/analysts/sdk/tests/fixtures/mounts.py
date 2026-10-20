"""Builders for a temporary container mount root and a validator over the repository's canonical contracts."""

from __future__ import annotations

import copy
import hashlib
import json
from collections.abc import Callable, Mapping
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT202012

CONTRACTS = Path(__file__).resolve().parents[5] / "contracts"
ANALYSIS = CONTRACTS / "analysis"
JOB_EXAMPLES = ANALYSIS / "analyst-job" / "v1" / "examples" / "valid"
ATTEMPT_EXAMPLES = ANALYSIS / "analyst-attempt" / "v1" / "examples" / "valid"

SEGMENT_JOB_EXAMPLE = "segment-job-with-window.json"
MATCH_JOB_EXAMPLE = "match-job-with-workflow.json"

# Document kind -> folder under contracts/analysis (schema file stem is the same).
_KIND_FOLDERS = {
    "analyst-job": "analyst-job",
    "analyst-attempt": "analyst-attempt",
    "result-manifest": "result-manifest",
    "fact-artifact": "fact-artifact",
    "upstream-results": "upstream-results",
}


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, document: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2), encoding="utf-8")


def sha256_digest(data: bytes) -> str:
    return "sha-256:" + hashlib.sha256(data).hexdigest()


def job_example(name: str = SEGMENT_JOB_EXAMPLE) -> dict[str, Any]:
    """A fresh copy of a valid analyst-job example from the canonical contracts."""
    return read_json(JOB_EXAMPLES / name)


def attempt_example() -> dict[str, Any]:
    return read_json(ATTEMPT_EXAMPLES / "attempt.json")


def _registry() -> Registry:
    paths = [CONTRACTS / "common" / "v1" / "common.schema.json"]
    paths += [ANALYSIS / folder / "v1" / f"{folder}.schema.json" for folder in _KIND_FOLDERS.values()]
    entries = [read_json(path) for path in paths]
    return Registry().with_resources(
        [(entry["$id"], Resource.from_contents(entry, DRAFT202012)) for entry in entries]
    )


_REGISTRY = _registry()
_FORMATS = FormatChecker()


def validate_canonical(kind: str, document: object, version: str | None = None) -> None:
    """Validate `document` against the canonical schema release in the repository's `contracts/`.

    Without `version`, the document's own `contractVersion` selects the release. Raises `AssertionError` listing
    the violations.
    """
    stated = version or (document.get("contractVersion") if isinstance(document, dict) else None)
    assert isinstance(stated, str), f"{kind} states no contract version"
    schema_path = ANALYSIS / _KIND_FOLDERS[kind] / "v1" / "releases" / f"{stated}.schema.json"
    assert schema_path.is_file(), f"no canonical {kind} release {stated}"
    validator = Draft202012Validator(read_json(schema_path), registry=_REGISTRY, format_checker=_FORMATS)
    found = sorted(validator.iter_errors(document), key=lambda error: list(map(str, error.absolute_path)))
    assert not found, f"{kind} {stated} violates the canonical schema: " + "; ".join(
        f"{error.message} at /" + "/".join(str(part) for part in error.absolute_path) for error in found[:10]
    )


def validate_written_json(kind: str, path: Path) -> Any:
    """Read a written document, validate it against the canonical schema, and return it."""
    document = read_json(path)
    validate_canonical(kind, document)
    return document


class Mount:
    """A temporary mount root laid out as the container interface describes it."""

    def __init__(self, root: Path) -> None:
        self.root = root
        self.result = root / "result"
        self.scratch = root / "scratch"
        self.inputs = root / "inputs"
        self.job_path = root / "job.json"
        self.attempt_path = root / "attempt.json"

    def write_job(self, job: Mapping[str, Any]) -> None:
        write_json(self.job_path, job)

    def write_attempt(self, attempt: Mapping[str, Any]) -> None:
        write_json(self.attempt_path, attempt)

    def stage_coverage(self, capability_id: str, coverage: Mapping[str, Any]) -> Path:
        """Write `inputs/{capabilityId}/coverage.json` holding the `upstream` and `coverage` parts."""
        path = self.inputs / capability_id / "coverage.json"
        write_json(path, coverage)
        return path

    def stage_result(
        self,
        capability_id: str,
        accepted_result_id: str,
        manifest: Mapping[str, Any],
        artifacts: Mapping[str, bytes] | None = None,
    ) -> Path:
        """Write a staged accepted result (its manifest and artifact bytes) and return the manifest path."""
        folder = self.inputs / capability_id / accepted_result_id
        folder.mkdir(parents=True, exist_ok=True)
        manifest_path = folder / "manifest.json"
        manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        for name, data in (artifacts or {}).items():
            (folder / name).write_bytes(data)
        return manifest_path

    def result_entries(self) -> list[str]:
        """Sorted names directly under `result/`, including a leftover staging directory."""
        return sorted(entry.name for entry in self.result.iterdir()) if self.result.exists() else []


Adjust = Callable[[dict[str, Any]], None]


def build_mount(
    root: Path,
    *,
    job: Mapping[str, Any] | None = None,
    job_example_name: str = SEGMENT_JOB_EXAMPLE,
    adjust_job: Adjust | None = None,
    attempt: Mapping[str, Any] | None = None,
    adjust_attempt: Adjust | None = None,
) -> Mount:
    """Create `job.json`, `attempt.json`, `result/`, and `scratch/` under `root`.

    The job defaults to a copy of a valid analyst-job example; `adjust_job` and `adjust_attempt` mutate the copies
    before they are written. Staged inputs are added afterwards through the returned mount.
    """
    mount = Mount(root)
    root.mkdir(parents=True, exist_ok=True)
    job_document = copy.deepcopy(dict(job)) if job is not None else job_example(job_example_name)
    if adjust_job:
        adjust_job(job_document)
    attempt_document = copy.deepcopy(dict(attempt)) if attempt is not None else attempt_example()
    if adjust_attempt:
        adjust_attempt(attempt_document)
    mount.write_job(job_document)
    mount.write_attempt(attempt_document)
    mount.result.mkdir()
    mount.scratch.mkdir()
    return mount
