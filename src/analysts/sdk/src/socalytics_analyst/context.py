"""Loading of the container interface (`job.json`, `attempt.json`) and the Analyst context."""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, TypeVar

from pydantic import BaseModel, ValidationError

from . import errors, schemas
from .inputs import load_inputs
from .media import SegmentMedia, load_media
from .models.attempt import AttemptContext
from .models.job import LogicalJob
from .models.manifest import AnalystManifest

DEFAULT_ROOT = Path("/socalytics")
JOB_FILE = "job.json"
ATTEMPT_FILE = "attempt.json"
SCRATCH_DIRECTORY = "scratch"
MANIFEST_ENVIRONMENT_VARIABLE = "SOCALYTICS_ANALYST_MANIFEST"
DEFAULT_MANIFEST_PATH = Path("/opt/socalytics/analyst-manifest.json")

_Model = TypeVar("_Model", bound=BaseModel)


@dataclass(frozen=True)
class AnalystContext:
    """Everything an Analyst receives; `result` is the only mutable member."""

    job: LogicalJob
    attempt: AttemptContext
    result: Any
    scratch: Path
    inputs: Any = ()
    media: SegmentMedia | None = None


def _read_document(root: Path, name: str) -> object:
    path = root / name
    try:
        raw = path.read_bytes()
    except OSError:
        raise errors.InputError(errors.INPUT_MISSING, f"{name} is absent or unreadable.") from None
    try:
        return json.loads(raw)
    except (ValueError, RecursionError):
        raise errors.InputError(errors.INPUT_INVALID, f"{name} is not valid JSON.") from None


def _load(root: Path, name: str, kind: str, model: type[_Model]) -> _Model:
    document = _read_document(root, name)
    schemas.validate(kind, document)
    try:
        return model.model_validate(document)
    except ValidationError as exc:
        locations = "; ".join(
            f"{error['type']} at /" + "/".join(str(part) for part in error["loc"]) for error in exc.errors()[:10]
        )
        raise errors.InputError(errors.INPUT_INVALID, f"{kind} violates its model: {locations}.") from None


def load_job(root: Path = DEFAULT_ROOT) -> LogicalJob:
    """Load `job.json`; the job must carry the run's workflow and, when segment-scoped, its segment window."""
    job = _load(root, JOB_FILE, "analyst-job", LogicalJob)
    if job.workflow is None:
        raise errors.InputError(errors.JOB_CONTEXT_INCOMPLETE, "the job does not state its workflow.")
    if job.execution_scope == "segment" and job.segment_window is None:
        raise errors.InputError(errors.JOB_CONTEXT_INCOMPLETE, "the segment job does not state its segment window.")
    return job


def load_attempt(root: Path = DEFAULT_ROOT) -> AttemptContext:
    """Load `attempt.json`."""
    return _load(root, ATTEMPT_FILE, "analyst-attempt", AttemptContext)


def load_documents(root: Path = DEFAULT_ROOT) -> tuple[LogicalJob, AttemptContext]:
    """Load and verify the job and the attempt context from the mount `root`."""
    return load_job(root), load_attempt(root)


def load_manifest(path: Path | None = None) -> AnalystManifest | None:
    """Load the Analyst's own manifest; no file means no declared schemas, an unreadable or invalid one is an input error.

    The location is `path`, else `SOCALYTICS_ANALYST_MANIFEST`, else `/opt/socalytics/analyst-manifest.json`.
    """
    if path is None:
        configured = os.environ.get(MANIFEST_ENVIRONMENT_VARIABLE)
        path = Path(configured) if configured else DEFAULT_MANIFEST_PATH
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        return None
    except OSError:
        raise errors.InputError(errors.INPUT_INVALID, "the analyst manifest is unreadable.") from None
    try:
        document = json.loads(raw)
    except (ValueError, RecursionError):
        raise errors.InputError(errors.INPUT_INVALID, "the analyst manifest is not valid JSON.") from None
    try:
        return AnalystManifest.model_validate(document)
    except ValidationError as exc:
        locations = "; ".join(
            f"{error['type']} at /" + "/".join(str(part) for part in error["loc"]) for error in exc.errors()[:10]
        )
        raise errors.InputError(errors.INPUT_INVALID, f"the analyst manifest violates its model: {locations}.") from None


def scratch_path(root: Path = DEFAULT_ROOT) -> Path:
    return root / SCRATCH_DIRECTORY


def build_context(
    root: Path, job: LogicalJob, attempt: AttemptContext, result: Any, inputs: Any = None, media: Any = None
) -> AnalystContext:
    """Assemble the context from loaded documents and the result writer; inputs and media load unless given."""
    if inputs is None:
        inputs = load_inputs(root, job)
    if media is None:
        media = load_media(root, job)
    return AnalystContext(
        job=job, attempt=attempt, result=result, scratch=scratch_path(root), inputs=inputs, media=media
    )
