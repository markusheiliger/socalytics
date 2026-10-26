from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest
from socalytics_analyst import schemas
from socalytics_analyst.models.attempt import AttemptContext
from socalytics_analyst.models.job import LogicalJob
from socalytics_analyst.models.segment import SegmentManifest

from analyst_evaluation_harness.staging import StagingError, stage_mount
from support.builders import clip_bytes, digest_of


def _stage(run_folder: Path, clip_file: Path, name: str = "mount-1", **overrides):
    options = {
        "clip_file": clip_file,
        "match_start_ms": 10000,
        "duration_ms": 5000,
        "capability_id": "test-capability",
        "capability_version": "1.0.0",
        "output_type": "foundational-facts",
    }
    options.update(overrides)
    return stage_mount(run_folder, name, **options)


@pytest.fixture
def run_folder(tmp_path: Path) -> Path:
    folder = tmp_path / "run"
    folder.mkdir()
    return folder


@pytest.fixture
def clip_file(tmp_path: Path) -> Path:
    path = tmp_path / "clip.bin"
    path.write_bytes(clip_bytes("clip"))
    return path


def _tree(root: Path) -> set[str]:
    return {str(path.relative_to(root)) for path in root.rglob("*")}


def test_mount_layout(run_folder: Path, clip_file: Path) -> None:
    mount = _stage(run_folder, clip_file)

    assert mount.root == run_folder / "mount-1"
    assert mount.output_type == "foundational-facts"
    assert mount.job_path.is_file()
    assert mount.attempt_path.is_file()
    assert mount.manifest_path == mount.root / "media" / "segment-manifest.json"
    assert mount.media_path == mount.root / "media" / "segment.mp4"
    assert mount.manifest_path.is_file()
    assert mount.media_path.read_bytes() == clip_file.read_bytes()
    assert mount.result.is_dir() and not any(mount.result.iterdir())
    assert mount.scratch.is_dir() and not any(mount.scratch.iterdir())
    assert _tree(mount.root) == {
        "job.json",
        "attempt.json",
        "media",
        "media/segment-manifest.json",
        "media/segment.mp4",
        "result",
        "scratch",
    }


def test_job_and_attempt_are_accepted_by_the_sdk_models(run_folder: Path, clip_file: Path) -> None:
    mount = _stage(run_folder, clip_file, attempt_number=2, schema_version="1.1.0")

    job_document = json.loads(mount.job_path.read_text(encoding="utf-8"))
    attempt_document = json.loads(mount.attempt_path.read_text(encoding="utf-8"))
    manifest_document = json.loads(mount.manifest_path.read_text(encoding="utf-8"))

    schemas.validate("analyst-job", job_document)
    schemas.validate("analyst-attempt", attempt_document)
    schemas.validate("segment-manifest", manifest_document)
    job = LogicalJob.model_validate(job_document)
    attempt = AttemptContext.model_validate(attempt_document)
    SegmentManifest.model_validate(manifest_document)

    assert job_document["contractVersion"] == "1.2.0"
    assert job_document["capability"] == {"id": "test-capability", "version": "1.0.0"}
    assert job_document["schemaVersion"] == "1.1.0"
    assert job_document["segmentWindow"]["startMs"] == 10000
    assert job_document["segmentWindow"]["endMs"] == 15000
    assert attempt_document["attemptNumber"] == 2
    assert job is not None and attempt is not None


def test_job_pins_the_manifest_and_manifest_pins_the_media(run_folder: Path, clip_file: Path) -> None:
    mount = _stage(run_folder, clip_file)

    job = json.loads(mount.job_path.read_text(encoding="utf-8"))
    manifest = json.loads(mount.manifest_path.read_text(encoding="utf-8"))

    assert job["segmentMedia"]["manifestChecksum"] == "sha-256:" + hashlib.sha256(
        mount.manifest_path.read_bytes()
    ).hexdigest()
    assert manifest["media"]["digest"] == digest_of(mount.media_path.read_bytes())
    assert manifest["media"]["sizeBytes"] == mount.media_path.stat().st_size
    assert manifest["nominalWindow"] == {"startMs": 10000, "endMs": 15000}


def test_every_execution_gets_a_fresh_directory(run_folder: Path, clip_file: Path) -> None:
    first = _stage(run_folder, clip_file, "mount-1")
    (first.result / "leftover.txt").write_text("left over", encoding="utf-8")
    second = _stage(run_folder, clip_file, "mount-2")

    assert first.root != second.root
    assert not any(second.result.iterdir())
    assert not any(second.scratch.iterdir())

    with pytest.raises(StagingError, match="already exists"):
        _stage(run_folder, clip_file, "mount-1")
    assert (first.result / "leftover.txt").read_text(encoding="utf-8") == "left over"


def test_nothing_is_written_outside_the_run_folder(tmp_path: Path, run_folder: Path, clip_file: Path) -> None:
    def outside() -> set[str]:
        return {item for item in _tree(tmp_path) if item != "run" and not item.startswith("run/")}

    before = outside()

    _stage(run_folder, clip_file)

    assert outside() == before


def test_refusals_leave_no_mount_behind(run_folder: Path, clip_file: Path) -> None:
    with pytest.raises(StagingError, match="output type"):
        _stage(run_folder, clip_file, output_type="unknown")
    with pytest.raises(StagingError, match="single path component"):
        _stage(run_folder, clip_file, "../escape")
    with pytest.raises(StagingError, match="single path component"):
        _stage(run_folder, clip_file, "nested/mount")
    with pytest.raises(StagingError, match="positive duration"):
        _stage(run_folder, clip_file, duration_ms=0)
    with pytest.raises(StagingError, match="not a readable file"):
        _stage(run_folder, run_folder / "missing.bin")

    assert not any(run_folder.iterdir())
    assert not (run_folder.parent / "escape").exists()


def test_missing_run_folder_is_refused(tmp_path: Path, clip_file: Path) -> None:
    with pytest.raises(StagingError, match="does not exist"):
        _stage(tmp_path / "absent", clip_file)
    assert not (tmp_path / "absent").exists()


def test_empty_clip_is_refused(run_folder: Path, tmp_path: Path) -> None:
    empty = tmp_path / "empty.bin"
    empty.write_bytes(b"")

    with pytest.raises(StagingError, match="empty"):
        _stage(run_folder, empty)


@pytest.mark.parametrize("output_type", ["foundational-facts", "soccer-concepts"])
def test_output_type_is_recorded_from_the_subject(run_folder: Path, clip_file: Path, output_type: str) -> None:
    assert _stage(run_folder, clip_file, output_type=output_type).output_type == output_type
