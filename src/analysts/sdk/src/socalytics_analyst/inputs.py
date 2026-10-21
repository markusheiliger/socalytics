"""Staged upstream inputs: loaded from the read-only `inputs/` mount and verified against the job's input snapshot."""

from __future__ import annotations

import hashlib
import json
from collections.abc import Iterator, Mapping
from dataclasses import dataclass
from pathlib import Path
from types import MappingProxyType

from pydantic import BaseModel, ValidationError

from . import errors, schemas
from .models.common import AcceptedResultLineage, CapabilityRef
from .models.facts import FACTS_MEDIA_TYPE, Fact, FactArtifact
from .models.job import InputEntry, LogicalJob
from .models.manifest import ResultManifest
from .models.upstream import StagedCoverage

INPUTS_DIRECTORY = "inputs"
COVERAGE_FILE = "coverage.json"
MANIFEST_FILE = "manifest.json"
_CHUNK = 1024 * 1024
_UPSTREAM_KIND = "upstream-results"


@dataclass(frozen=True)
class StagedResult:
    """One verified accepted upstream result; `artifacts` maps artifact names to read-only paths."""

    accepted_result_id: str
    manifest: ResultManifest
    facts: tuple[Fact, ...]
    artifacts: Mapping[str, Path]


@dataclass(frozen=True)
class StagedDependency:
    """One upstream dependency of the job; `results` is empty unless the input snapshot status is `accepted`."""

    upstream_node_key: str
    capability: CapabilityRef
    edge_kind: str
    status: str
    coverage: StagedCoverage | None
    results: tuple[StagedResult, ...]


@dataclass(frozen=True)
class StagedInputs:
    """One `StagedDependency` per entry of the job's `inputs`, verified on load."""

    dependencies: tuple[StagedDependency, ...] = ()

    def __iter__(self) -> Iterator[StagedDependency]:
        return iter(self.dependencies)

    def __len__(self) -> int:
        return len(self.dependencies)

    def __bool__(self) -> bool:
        return bool(self.dependencies)

    def get(self, capability_id: str) -> StagedDependency | None:
        """The dependency on the capability `capability_id`, or `None`."""
        return next((item for item in self.dependencies if item.capability.id == capability_id), None)

    def by_node_key(self, node_key: str) -> StagedDependency | None:
        return next((item for item in self.dependencies if item.upstream_node_key == node_key), None)


def _mismatch(message: str) -> errors.InputError:
    return errors.InputError(errors.INPUT_STAGING_MISMATCH, message)


def _read_bytes(path: Path, what: str) -> bytes:
    try:
        return path.read_bytes()
    except OSError:
        raise errors.InputError(errors.INPUT_MISSING, f"{what} is absent or unreadable.") from None


def _parse_json(raw: bytes, what: str) -> object:
    try:
        return json.loads(raw)
    except (ValueError, RecursionError):
        raise errors.InputError(errors.INPUT_INVALID, f"{what} is not valid JSON.") from None


def _model(model: type[BaseModel], document: object, what: str):
    try:
        return model.model_validate(document)
    except ValidationError as exc:
        locations = "; ".join(
            f"{error['type']} at /" + "/".join(str(part) for part in error["loc"]) for error in exc.errors()[:10]
        )
        raise errors.InputError(errors.INPUT_INVALID, f"{what} violates its model: {locations}.") from None


def _digest_and_size(path: Path) -> tuple[str, int]:
    hasher = hashlib.sha256()
    size = 0
    with path.open("rb") as handle:
        while chunk := handle.read(_CHUNK):
            hasher.update(chunk)
            size += len(chunk)
    return f"sha-256:{hasher.hexdigest()}", size


def _load_coverage(folder: Path, entry: InputEntry, *, required: bool) -> StagedCoverage | None:
    path = folder / COVERAGE_FILE
    if not required and not path.is_file():
        return None
    what = f"inputs/{entry.capability.id}/{COVERAGE_FILE}"
    document = _parse_json(_read_bytes(path, what), what)
    if not isinstance(document, dict):
        raise errors.InputError(errors.INPUT_INVALID, f"{what} is not a JSON object.")
    schemas.validate_fragment(_UPSTREAM_KIND, "/properties/upstream", document.get("upstream"), label=f"{what} upstream")
    coverage = document.get("coverage")
    if not isinstance(coverage, list):
        raise errors.InputError(errors.INPUT_INVALID, f"{what} coverage is not an array.")
    for item in coverage:
        schemas.validate_fragment(_UPSTREAM_KIND, "/$defs/coverageEntry", item, label=f"{what} coverage entry")
    staged = _model(StagedCoverage, document, what)
    if staged.upstream.capability != entry.capability or staged.upstream.edge_kind != entry.edge_kind:
        raise _mismatch(f"{what} describes another dependency than the input snapshot entry.")
    return staged


def _verify_artifacts(folder: Path, manifest: ResultManifest) -> Mapping[str, Path]:
    paths: dict[str, Path] = {}
    for artifact in manifest.artifacts:
        path = folder / artifact.name
        if path.is_symlink() or not path.is_file():
            raise errors.InputError(errors.INPUT_ARTIFACT_MISMATCH, f"a staged artifact {artifact.name} is missing.")
        try:
            digest, size = _digest_and_size(path)
        except OSError:
            raise errors.InputError(
                errors.INPUT_ARTIFACT_MISMATCH, f"a staged artifact {artifact.name} is unreadable."
            ) from None
        if size != artifact.size_bytes or digest != artifact.digest:
            raise errors.InputError(
                errors.INPUT_ARTIFACT_MISMATCH, f"a staged artifact {artifact.name} differs in size or digest."
            )
        paths[artifact.name] = path
    return MappingProxyType(paths)


def _read_facts(manifest: ResultManifest, artifact_paths: Mapping[str, Path]) -> tuple[Fact, ...]:
    facts: list[Fact] = []
    for artifact in manifest.artifacts:
        if artifact.media_type != FACTS_MEDIA_TYPE:
            continue
        what = f"fact artifact {artifact.name}"
        document = _parse_json(_read_bytes(artifact_paths[artifact.name], what), what)
        schemas.validate("fact-artifact", document)
        facts.extend(_model(FactArtifact, document, what).facts)
    return tuple(facts)


def _load_result(folder: Path, lineage: AcceptedResultLineage) -> StagedResult:
    what = f"manifest of accepted result {lineage.accepted_result_id}"
    raw = _read_bytes(folder / MANIFEST_FILE, what)
    if f"sha-256:{hashlib.sha256(raw).hexdigest()}" != lineage.manifest_checksum:
        raise _mismatch(f"the {what} differs from its manifest checksum.")
    document = _parse_json(raw, what)
    schemas.validate("result-manifest", document)
    manifest = _model(ResultManifest, document, what)
    artifact_paths = _verify_artifacts(folder, manifest)
    return StagedResult(lineage.accepted_result_id, manifest, _read_facts(manifest, artifact_paths), artifact_paths)


def _result_folders(folder: Path) -> set[str]:
    if not folder.is_dir():
        return set()
    return {child.name for child in folder.iterdir() if child.is_dir() or child.is_symlink()}


def _load_dependency(root: Path, entry: InputEntry) -> StagedDependency:
    folder = root / entry.capability.id
    staged_ids = _result_folders(folder)
    expected = [lineage.accepted_result_id for lineage in entry.accepted_results]
    if staged_ids != set(expected):
        raise _mismatch(f"the staged results of {entry.capability.id} differ from the input snapshot.")
    coverage = _load_coverage(folder, entry, required=entry.status == "accepted")
    results = tuple(_load_result(folder / lineage.accepted_result_id, lineage) for lineage in entry.accepted_results)
    return StagedDependency(entry.upstream_node_key, entry.capability, entry.edge_kind, entry.status, coverage, results)


def load_inputs(root: Path, job: LogicalJob) -> StagedInputs:
    """Load and verify the staged inputs under `root / "inputs"` against the job's input snapshot.

    An absent input mount is valid only when the snapshot has no accepted result. Raises `InputError`
    (`input-staging-mismatch`, `input-artifact-mismatch`, `input-missing`, `input-invalid`). Files are only read.
    """
    inputs_root = root / INPUTS_DIRECTORY
    declared = {entry.capability.id for entry in job.inputs}
    if inputs_root.is_dir():
        extra = {child.name for child in inputs_root.iterdir()} - declared
        if extra:
            raise _mismatch("the input mount holds a dependency the job does not declare.")
    elif any(entry.status == "accepted" for entry in job.inputs):
        raise _mismatch("the input mount is absent although the input snapshot has accepted results.")
    return StagedInputs(tuple(_load_dependency(inputs_root, entry) for entry in job.inputs))
