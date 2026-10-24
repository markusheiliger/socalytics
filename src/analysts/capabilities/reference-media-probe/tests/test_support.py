import json
import subprocess
from collections.abc import Callable
from pathlib import Path

import pytest
from socalytics_analyst import schemas

from support import media as media_support
from support.mounts import Mount, digest_of

WINDOW = {"startMs": 0, "endMs": 5000}
SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]


def _probe(path: Path) -> dict:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
         "stream=codec_name,width,height,r_frame_rate:format=duration", "-of", "json", str(path)],
        capture_output=True, text=True, check=True,
    ).stdout
    return json.loads(out)


def _mount(media_factory: Callable[..., Path], mount_factory: Callable[..., Mount], **options: object) -> Mount:
    media = media_factory()
    return mount_factory(
        media,
        nominal_window=WINDOW,
        requested_source_interval=WINDOW,
        encoded_interval=WINDOW,
        timeline_spans=SPANS,
        **options,
    )


def test_synthetic_media_is_native_mpeg4_at_the_requested_rate(media_factory):
    probed = _probe(media_factory(duration_seconds=2, frame_rate=10))
    stream = probed["streams"][0]
    assert (stream["codec_name"], stream["width"], stream["height"], stream["r_frame_rate"]) == ("mpeg4", 64, 64, "10/1")
    assert float(probed["format"]["duration"]) == pytest.approx(2, abs=0.2)


def test_synthetic_media_applies_the_timestamp_offset(media_factory):
    start = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=start_time", "-of", "csv=p=0", str(media_factory(offset_seconds=3))],
        capture_output=True, text=True, check=True,
    ).stdout.strip()
    assert float(start) == pytest.approx(3, abs=0.1)


def test_missing_tool_fails_with_its_name(monkeypatch):
    monkeypatch.setenv("PATH", "")
    with pytest.raises(AssertionError, match="ffmpeg"):
        media_support.require_tools()


def test_job_validates_against_the_sdk_schema(media_factory, mount_factory):
    mount = _mount(media_factory, mount_factory)
    job = json.loads(mount.job_path.read_text(encoding="utf-8"))
    assert schemas.validate("analyst-job", job) == "1.2.0"
    assert job["capability"] == {"id": "reference-media-probe", "version": "1.0.0"}
    assert job["schemaVersion"] == "1.0.0"
    assert job["segmentMedia"] == {"videoPolicy": "required", "manifestChecksum": digest_of(mount.manifest_path.read_bytes())}


def test_job_without_segment_media_is_valid_and_omits_the_declaration(media_factory, mount_factory):
    mount = _mount(media_factory, mount_factory, with_segment_media=False)
    job = json.loads(mount.job_path.read_text(encoding="utf-8"))
    schemas.validate("analyst-job", job)
    assert "segmentMedia" not in job


def test_attempt_validates_against_the_sdk_schema(media_factory, mount_factory):
    mount = _mount(media_factory, mount_factory)
    assert schemas.validate("analyst-attempt", json.loads(mount.attempt_path.read_text(encoding="utf-8"))) == "1.0.0"


def test_manifest_validates_and_states_the_real_digest_and_size(media_factory, mount_factory):
    mount = _mount(media_factory, mount_factory)
    manifest = json.loads(mount.manifest_path.read_text(encoding="utf-8"))
    assert schemas.validate("segment-manifest", manifest) == "1.0.0"
    data = mount.media_path.read_bytes()
    assert manifest["media"]["digest"] == digest_of(data)
    assert manifest["media"]["sizeBytes"] == len(data)


def test_mount_has_empty_result_and_scratch_folders(media_factory, mount_factory):
    mount = _mount(media_factory, mount_factory)
    assert mount.result_entries() == []
    assert list(mount.scratch.iterdir()) == []
