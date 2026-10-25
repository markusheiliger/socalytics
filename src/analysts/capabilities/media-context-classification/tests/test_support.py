from __future__ import annotations

import json
import subprocess
from collections.abc import Callable
from pathlib import Path

import numpy as np
import pytest
from socalytics_analyst import schemas

from support import media as media_support
from support.mounts import Mount, digest_of

WINDOW = {"startMs": 0, "endMs": 5000}
SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]


def _raw_frames(path: Path) -> np.ndarray:
    data = subprocess.run(
        ["ffmpeg", "-v", "error", "-nostdin", "-i", str(path), "-map", "0:v:0", "-fps_mode", "passthrough",
         "-pix_fmt", "bgr24", "-f", "rawvideo", "-"],
        capture_output=True, check=True,
    ).stdout
    size = media_support.WIDTH * media_support.HEIGHT * 3
    return np.frombuffer(data, dtype=np.uint8).reshape(-1, media_support.HEIGHT, media_support.WIDTH, 3)[: len(data) // size]


def _first_timestamp_ms(path: Path) -> float:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=best_effort_timestamp_time",
         "-of", "csv=p=0", str(path)],
        capture_output=True, text=True, check=True,
    ).stdout.split()
    return float(out[0].rstrip(",")) * 1000


def _video_streams(path: Path) -> str:
    return subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v", "-show_entries", "stream=index", "-of", "csv=p=0", str(path)],
        capture_output=True, text=True, check=True,
    ).stdout.strip()


def _mount(mount_factory: Callable[..., Mount], media: Path, **options: object) -> Mount:
    return mount_factory(
        media,
        nominal_window=WINDOW,
        requested_source_interval=WINDOW,
        encoded_interval=WINDOW,
        timeline_spans=SPANS,
        **options,
    )


def _jumps(frames: np.ndarray, minimum: float) -> list[int]:
    steps = np.abs(np.diff(frames.astype(np.int16), axis=0)).mean(axis=(1, 2, 3))
    return [int(index) + 1 for index in np.flatnonzero(steps > minimum)]


def test_hard_cuts_clip_has_the_expected_frame_count_and_cut_positions(tmp_path):
    clip = media_support.hard_cuts_clip(tmp_path)
    frames = _raw_frames(clip.path)
    assert len(frames) == clip.frame_count == 100
    assert clip.cut_indexes == (25, 50, 75)
    assert _jumps(frames, 20) == list(clip.cut_indexes)


def test_second_frame_cut_is_between_the_first_and_second_frame(tmp_path):
    clip = media_support.second_frame_cut_clip(tmp_path)
    assert clip.cut_indexes == (1,)
    assert _jumps(_raw_frames(clip.path), 20) == [1]


def test_closely_spaced_cuts_are_five_frames_apart(tmp_path):
    clip = media_support.closely_spaced_cuts_clip(tmp_path)
    assert clip.cut_indexes == (20, 25)
    assert _jumps(_raw_frames(clip.path), 20) == [20, 25]


def test_clips_without_a_cut_have_no_cut_indexes_and_no_jumps(tmp_path):
    for factory in (media_support.no_cut_clip, media_support.static_noise_clip, media_support.steady_pan_clip):
        clip = factory(tmp_path, name=f"{factory.__name__}.mp4")
        assert clip.cut_indexes == ()
        assert len(_raw_frames(clip.path)) == clip.frame_count
        assert _jumps(_raw_frames(clip.path), 20) == []


def test_low_contrast_cut_stays_below_the_hard_jump(tmp_path):
    clip = media_support.low_contrast_cut_clip(tmp_path)
    assert clip.cut_indexes == (25,)
    frames = _raw_frames(clip.path)
    assert _jumps(frames, 10) == [25]
    assert _jumps(frames, 25) == []


def test_single_frame_clip_has_one_frame(tmp_path):
    clip = media_support.single_frame_clip(tmp_path)
    assert clip.frame_count == 1
    assert len(_raw_frames(clip.path)) == 1


def test_lead_in_clip_records_the_lead_in_and_the_cut_position(tmp_path):
    clip = media_support.lead_in_clip(tmp_path)
    assert clip.lead_in_frames == 5
    assert clip.cut_indexes == (5,)
    assert clip.frame_count == 30
    cut_inside = media_support.lead_in_clip(tmp_path, cut_in_lead_in=True, name="inside.mp4")
    assert cut_inside.cut_indexes == (2,)
    assert cut_inside.frame_count == 30


def test_frame_ms_includes_the_frame_rate_and_the_offset(tmp_path):
    clip = media_support.hard_cuts_clip(tmp_path, offset_seconds=3.0)
    assert clip.frame_ms(0) == 3000
    assert clip.frame_ms(25) == 4000
    assert _first_timestamp_ms(clip.path) == pytest.approx(3000, abs=1)


def test_empty_video_and_audio_only_media_are_generated(tmp_path):
    empty = media_support.create_empty_video_clip(tmp_path)
    assert empty.stat().st_size > 0
    assert _video_streams(empty) == ""
    audio = media_support.create_audio_only_media(tmp_path)
    assert _video_streams(audio) == ""


def test_missing_tool_fails_with_its_name(monkeypatch):
    monkeypatch.setenv("PATH", "")
    with pytest.raises(AssertionError, match="ffmpeg"):
        media_support.require_tools()


def test_job_validates_against_the_sdk_schema(tmp_path, mount_factory):
    mount = _mount(mount_factory, media_support.hard_cuts_clip(tmp_path).path)
    job = json.loads(mount.job_path.read_text(encoding="utf-8"))
    assert schemas.validate("analyst-job", job) == "1.2.0"
    assert job["capability"] == {"id": "media-context-classification", "version": "1.0.0"}
    assert job["segmentMedia"] == {"videoPolicy": "required", "manifestChecksum": digest_of(mount.manifest_path.read_bytes())}


def test_job_without_segment_media_omits_the_declaration(tmp_path, mount_factory):
    mount = _mount(mount_factory, media_support.no_cut_clip(tmp_path).path, with_segment_media=False)
    job = json.loads(mount.job_path.read_text(encoding="utf-8"))
    schemas.validate("analyst-job", job)
    assert "segmentMedia" not in job
    assert mount.media_path.is_file()


def test_attempt_and_manifest_validate_and_state_the_real_digest_and_size(tmp_path, mount_factory):
    mount = _mount(mount_factory, media_support.hard_cuts_clip(tmp_path).path)
    assert schemas.validate("analyst-attempt", json.loads(mount.attempt_path.read_text(encoding="utf-8"))) == "1.0.0"
    manifest = json.loads(mount.manifest_path.read_text(encoding="utf-8"))
    assert schemas.validate("segment-manifest", manifest) == "1.0.0"
    data = mount.media_path.read_bytes()
    assert manifest["media"]["digest"] == digest_of(data)
    assert manifest["media"]["sizeBytes"] == len(data)


def test_mount_supports_a_non_zero_segment_offset(tmp_path, mount_factory):
    clip = media_support.hard_cuts_clip(tmp_path, offset_seconds=10.0)
    window = {"startMs": 10_000, "endMs": 15_000}
    mount = mount_factory(
        clip.path,
        nominal_window=window,
        requested_source_interval=window,
        encoded_interval=window,
        timeline_spans=[{"mediaStartMs": 10_000, "mediaEndMs": 15_000, "matchStartMs": 60_000}],
    )
    manifest = json.loads(mount.manifest_path.read_text(encoding="utf-8"))
    schemas.validate("segment-manifest", manifest)
    assert manifest["nominalWindow"] == window
    assert manifest["timelineSpans"][0]["matchStartMs"] == 60_000
    job = json.loads(mount.job_path.read_text(encoding="utf-8"))
    assert job["segmentWindow"]["startMs"] == 10_000


def test_mount_has_empty_result_and_scratch_folders(tmp_path, mount_factory):
    mount = _mount(mount_factory, media_support.no_cut_clip(tmp_path).path)
    assert mount.result_entries() == []
    assert list(mount.scratch.iterdir()) == []
