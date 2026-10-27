"""Manager-style staging: one fresh mount root per execution, laid out as the SDK's container interface expects."""

from __future__ import annotations

import hashlib
import json
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from socalytics_analyst import errors as sdk_errors
from socalytics_analyst import schemas
from socalytics_analyst.models.attempt import AttemptContext
from socalytics_analyst.models.job import LogicalJob
from socalytics_analyst.models.segment import SegmentManifest

JOB_CONTRACT_VERSION = "1.2.0"
OUTPUT_TYPES = ("foundational-facts", "soccer-concepts")

_ZERO = "0" * 64
_CREATED_AT = "2026-01-01T00:00:00Z"
_CHUNK = 1024 * 1024


class StagingError(Exception):
    """The mount root cannot be staged; the message names the cause."""


@dataclass(frozen=True)
class StagedMount:
    """The paths of one staged mount root."""

    root: Path
    output_type: str

    @property
    def job_path(self) -> Path:
        return self.root / "job.json"

    @property
    def attempt_path(self) -> Path:
        return self.root / "attempt.json"

    @property
    def media_folder(self) -> Path:
        return self.root / "media"

    @property
    def manifest_path(self) -> Path:
        return self.media_folder / "segment-manifest.json"

    @property
    def media_path(self) -> Path:
        return self.media_folder / "segment.mp4"

    @property
    def result(self) -> Path:
        return self.root / "result"

    @property
    def scratch(self) -> Path:
        return self.root / "scratch"


def _digest_and_size(path: Path) -> tuple[str, int]:
    hasher, size = hashlib.sha256(), 0
    with path.open("rb") as stream:
        while chunk := stream.read(_CHUNK):
            hasher.update(chunk)
            size += len(chunk)
    return f"sha-256:{hasher.hexdigest()}", size


def _json_bytes(document: object) -> bytes:
    return json.dumps(document, indent=2, sort_keys=True, ensure_ascii=False).encode("utf-8")


def _fixed_digest(last: str) -> str:
    return "sha-256:" + _ZERO[: 64 - len(last)] + last


def _job(
    capability_id: str,
    capability_version: str,
    schema_version: str,
    start_ms: int,
    end_ms: int,
    manifest_checksum: str,
) -> dict[str, Any]:
    return {
        "contractVersion": JOB_CONTRACT_VERSION,
        "messageId": "evaluation-message",
        "readinessOccurrenceId": "evaluation-readiness",
        "readinessSequence": 1,
        "runId": "evaluation-run",
        "nodeId": "evaluation-node",
        "nodeKey": capability_id,
        "logicalJobId": "evaluation-job",
        "teamId": "evaluation-team",
        "matchId": "evaluation-match",
        "recordingSetVersionId": "evaluation-rsv",
        "capability": {"id": capability_id, "version": capability_version},
        "tier": "low-level",
        "schemaVersion": schema_version,
        "executionScope": "segment",
        "segment": {
            "recordingSetVersionId": "evaluation-rsv",
            "recordingVersionId": "evaluation-rv",
            "timelineMappingDigest": _fixed_digest("1"),
            "segmentationPolicyDigest": _fixed_digest("2"),
            "segmentNumber": 1,
        },
        "segmentWindow": {"segmentId": "evaluation-segment", "startMs": start_ms, "endMs": end_ms},
        "segmentMedia": {"videoPolicy": "required", "manifestChecksum": manifest_checksum},
        "workflow": {"id": "evaluation-workflow", "version": "1.0.0"},
        "inputs": [],
        "pinned": {
            "analystProfileId": "evaluation-profile",
            "analystProfileDigest": _fixed_digest("3"),
            "imageDigest": _fixed_digest("4"),
            "modelDigest": None,
            "policies": [],
        },
        "resources": {"cpuMillicores": 1000, "memoryMebibytes": 512, "scratchDiskMebibytes": 256},
        "attemptBudget": {"maxAttempts": 1},
        "timeouts": {"leaseSeconds": 60, "staleTimeoutSeconds": 120, "executionTimeoutSeconds": 600},
        "createdAt": _CREATED_AT,
        "correlation": {"correlationId": "evaluation-correlation"},
    }


def _manifest(media_digest: str, media_size: int, duration_ms: int, match_start_ms: int) -> dict[str, Any]:
    interval = {"startMs": 0, "endMs": duration_ms}
    return {
        "contractVersion": "1.0.0",
        "identity": {
            "recordingVersionId": "evaluation-rv",
            "timelineMappingDigest": _fixed_digest("1"),
            "segmentationPolicyDigest": _fixed_digest("2"),
            "segmentNumber": 1,
        },
        "segmentationPolicy": {"version": 1, "durationSeconds": max(1, -(-duration_ms // 1000))},
        "nominalWindow": {"startMs": match_start_ms, "endMs": match_start_ms + duration_ms},
        "requestedSourceInterval": dict(interval),
        "encodedInterval": dict(interval),
        "timelineSpans": [{"mediaStartMs": 0, "mediaEndMs": duration_ms, "matchStartMs": match_start_ms}],
        "media": {
            "artifactReference": "segment.mp4",
            "mediaType": "video/mp4",
            "digest": media_digest,
            "sizeBytes": media_size,
        },
        "producer": {"toolchain": "analyst-evaluation-harness"},
        "createdAt": _CREATED_AT,
    }


def _validated(kind: str, model: type, document: dict[str, Any]) -> None:
    try:
        schemas.validate(kind, document)
        model.model_validate(document)
    except sdk_errors.InputError as exc:
        raise StagingError(f"the staged {kind} is invalid: {exc.detail}") from None
    except ValueError as exc:
        raise StagingError(f"the staged {kind} is invalid: {exc}") from None


def stage_mount(
    run_folder: Path,
    name: str,
    *,
    clip_file: Path,
    match_start_ms: int,
    duration_ms: int,
    capability_id: str,
    capability_version: str,
    output_type: str,
    schema_version: str = "1.0.0",
    attempt_number: int = 1,
) -> StagedMount:
    """Stage `run_folder / name` as a fresh mount root for one execution of the clip.

    `output_type` is the subject's; `attempt.json` cannot carry it, so the staged mount records it for the runners'
    SDK entry point call. The directory must not exist yet and nothing is written outside the run folder.
    """
    if output_type not in OUTPUT_TYPES:
        raise StagingError(f"the output type {output_type!r} is not a supported output type.")
    if not name or name in (".", "..") or Path(name).name != name or "\\" in name:
        raise StagingError(f"the mount name {name!r} must be a single path component.")
    if duration_ms < 1 or match_start_ms < 0:
        raise StagingError("the clip needs a positive duration and a non-negative match start.")
    run_folder = Path(run_folder)
    if not run_folder.is_dir():
        raise StagingError(f"the run folder {run_folder} does not exist.")
    clip_file = Path(clip_file)
    if not clip_file.is_file():
        raise StagingError(f"the clip file {clip_file} is not a readable file.")

    root = run_folder / name
    try:
        root.mkdir()
    except FileExistsError:
        raise StagingError(f"the mount root {root} already exists; every execution needs a fresh one.") from None
    mount = StagedMount(root, output_type)

    try:
        mount.media_folder.mkdir()
        shutil.copyfile(clip_file, mount.media_path)
        media_digest, media_size = _digest_and_size(mount.media_path)
    except OSError as exc:
        raise StagingError(f"the clip file cannot be copied: {exc.strerror or exc}") from None
    if media_size < 1:
        raise StagingError("the clip file is empty.")

    manifest = _manifest(media_digest, media_size, duration_ms, match_start_ms)
    manifest_bytes = _json_bytes(manifest)
    checksum = f"sha-256:{hashlib.sha256(manifest_bytes).hexdigest()}"
    job = _job(
        capability_id, capability_version, schema_version, match_start_ms, match_start_ms + duration_ms, checksum
    )
    attempt = {"contractVersion": "1.0.0", "attemptId": "evaluation-attempt", "attemptNumber": attempt_number}

    _validated("segment-manifest", SegmentManifest, manifest)
    _validated("analyst-job", LogicalJob, job)
    _validated("analyst-attempt", AttemptContext, attempt)

    mount.manifest_path.write_bytes(manifest_bytes)
    mount.job_path.write_bytes(_json_bytes(job))
    mount.attempt_path.write_bytes(_json_bytes(attempt))
    mount.result.mkdir()
    mount.scratch.mkdir()
    return mount
