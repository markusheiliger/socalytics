"""Container mount roots for the capability: job, attempt, staged segment media, and empty output folders."""

from __future__ import annotations

import hashlib
import json
from collections.abc import Mapping, Sequence
from pathlib import Path
from typing import Any

CAPABILITY_ID = "media-context-classification"
CAPABILITY_VERSION = "1.0.0"
SCHEMA_VERSION = "1.0.0"
DIGEST_PREFIX = "sha-256:"

Interval = Mapping[str, int]


def digest_of(data: bytes) -> str:
    return DIGEST_PREFIX + hashlib.sha256(data).hexdigest()


def _write_json(path: Path, document: object) -> bytes:
    path.parent.mkdir(parents=True, exist_ok=True)
    raw = json.dumps(document, indent=2).encode("utf-8")
    path.write_bytes(raw)
    return raw


class Mount:
    """A mount root laid out as the container interface describes it."""

    def __init__(self, root: Path) -> None:
        self.root = root
        self.job_path = root / "job.json"
        self.attempt_path = root / "attempt.json"
        self.media_folder = root / "media"
        self.media_path = self.media_folder / "segment.mp4"
        self.manifest_path = self.media_folder / "segment-manifest.json"
        self.result = root / "result"
        self.scratch = root / "scratch"

    def result_entries(self) -> list[str]:
        return sorted(entry.name for entry in self.result.iterdir()) if self.result.exists() else []


def _job(nominal_window: Interval, manifest_checksum: str | None, schema_version: str) -> dict[str, Any]:
    job: dict[str, Any] = {
        "contractVersion": "1.2.0",
        "messageId": "msg-0001",
        "readinessOccurrenceId": "ready-0001",
        "readinessSequence": 1,
        "runId": "run-0001",
        "nodeId": "node-0001",
        "nodeKey": "classify-media-context",
        "logicalJobId": "job-0001",
        "teamId": "team-0001",
        "matchId": "match-0001",
        "recordingSetVersionId": "rsv-0001",
        "capability": {"id": CAPABILITY_ID, "version": CAPABILITY_VERSION},
        "tier": "low-level",
        "schemaVersion": schema_version,
        "executionScope": "segment",
        "segment": {
            "recordingSetVersionId": "rsv-0001",
            "recordingVersionId": "rv-0001",
            "timelineMappingDigest": "sha-256:" + "0" * 63 + "1",
            "segmentationPolicyDigest": "sha-256:" + "0" * 63 + "2",
            "segmentNumber": 1,
        },
        "segmentWindow": {
            "segmentId": "seg-0001",
            "startMs": nominal_window["startMs"],
            "endMs": nominal_window["endMs"],
        },
        "workflow": {"id": "media-context-analysis", "version": "1.0.0"},
        "inputs": [],
        "pinned": {
            "analystProfileId": "media-context-classification-dev",
            "analystProfileDigest": "sha-256:" + "0" * 63 + "3",
            "imageDigest": "sha-256:" + "0" * 63 + "4",
            "modelDigest": "sha-256:" + "0" * 64,
            "policies": [{"policyId": "retention", "version": "1.0.0"}],
        },
        "resources": {"cpuMillicores": 1000, "memoryMebibytes": 512, "scratchDiskMebibytes": 256},
        "attemptBudget": {"maxAttempts": 3},
        "timeouts": {"leaseSeconds": 60, "staleTimeoutSeconds": 120, "executionTimeoutSeconds": 600},
        "createdAt": "2026-10-20T10:00:00Z",
        "correlation": {"correlationId": "corr-0001"},
    }
    if manifest_checksum is not None:
        job["segmentMedia"] = {"videoPolicy": "required", "manifestChecksum": manifest_checksum}
    return job


def build_mount(
    root: Path,
    media: Path,
    *,
    nominal_window: Interval,
    requested_source_interval: Interval,
    encoded_interval: Interval,
    timeline_spans: Sequence[Mapping[str, int]],
    with_segment_media: bool = True,
    schema_version: str = SCHEMA_VERSION,
) -> Mount:
    """Write a mount root for the segment media at `media`.

    The manifest states the media's real digest and size. With `with_segment_media=False` the job carries no
    `segmentMedia`, although the media mount is still written. A non-zero `startMs` in the nominal window or
    timeline spans gives a non-zero segment offset.
    """
    mount = Mount(root)
    mount.media_folder.mkdir(parents=True, exist_ok=True)
    media_bytes = media.read_bytes()
    mount.media_path.write_bytes(media_bytes)
    manifest = {
        "contractVersion": "1.0.0",
        "identity": {
            "recordingVersionId": "rv-0001",
            "timelineMappingDigest": "sha-256:" + "0" * 63 + "1",
            "segmentationPolicyDigest": "sha-256:" + "0" * 63 + "2",
            "segmentNumber": 1,
        },
        "segmentationPolicy": {"version": 1, "durationSeconds": 5},
        "nominalWindow": dict(nominal_window),
        "requestedSourceInterval": dict(requested_source_interval),
        "encodedInterval": dict(encoded_interval),
        "timelineSpans": [dict(span) for span in timeline_spans],
        "media": {
            "artifactReference": "segment.mp4",
            "mediaType": "video/mp4",
            "digest": digest_of(media_bytes),
            "sizeBytes": len(media_bytes),
        },
        "producer": {"toolchain": "ffmpeg test"},
        "createdAt": "2026-10-20T10:00:00Z",
    }
    manifest_bytes = _write_json(mount.manifest_path, manifest)
    checksum = digest_of(manifest_bytes) if with_segment_media else None
    _write_json(mount.job_path, _job(nominal_window, checksum, schema_version))
    _write_json(mount.attempt_path, {"contractVersion": "1.0.0", "attemptId": "attempt-0001", "attemptNumber": 1})
    mount.result.mkdir(exist_ok=True)
    mount.scratch.mkdir(exist_ok=True)
    return mount
