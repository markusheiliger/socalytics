"""Loading of the container interface (`job.json`, `attempt.json`) and the Analyst context."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, TypeVar

from pydantic import BaseModel, ValidationError

from . import errors, schemas
from .models.attempt import AttemptContext
from .models.job import LogicalJob

DEFAULT_ROOT = Path("/socalytics")
JOB_FILE = "job.json"
ATTEMPT_FILE = "attempt.json"
SCRATCH_DIRECTORY = "scratch"

_Model = TypeVar("_Model", bound=BaseModel)


@dataclass(frozen=True)
class AnalystContext:
    """Everything an Analyst receives; `result` is the only mutable member."""

    job: LogicalJob
    attempt: AttemptContext
    result: Any
    scratch: Path
    inputs: Any = ()


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


def scratch_path(root: Path = DEFAULT_ROOT) -> Path:
    return root / SCRATCH_DIRECTORY


def build_context(root: Path, job: LogicalJob, attempt: AttemptContext, result: Any, inputs: Any = ()) -> AnalystContext:
    """Assemble the context from loaded documents and the result writer."""
    return AnalystContext(job=job, attempt=attempt, result=result, scratch=scratch_path(root), inputs=inputs)
