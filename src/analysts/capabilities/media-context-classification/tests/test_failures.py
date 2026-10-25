from __future__ import annotations

import shutil
from collections.abc import Callable, Mapping, Sequence
from pathlib import Path

import pytest
from socalytics_analyst import run

from media_context_classification.__main__ import analyse
from support import media as media_support
from support.mounts import Mount

WINDOW = {"startMs": 0, "endMs": 5000}
SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]
MEDIA_MARKER = b"MEDIA-CONTENT-MARKER"
MAX_STDERR_LINE = 200


def _mount(
    mount_factory: Callable[..., Mount],
    media: Path,
    spans: Sequence[Mapping[str, int]] = SPANS,
    **options: object,
) -> Mount:
    return mount_factory(
        media,
        nominal_window=WINDOW,
        requested_source_interval=WINDOW,
        encoded_interval=WINDOW,
        timeline_spans=spans,
        **options,
    )


def _run(mount: Mount, capsys: pytest.CaptureFixture[str]) -> tuple[int, str]:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0, capsys.readouterr().err


def _assert_failed_with(mount: Mount, stderr: str, reason: str) -> None:
    assert mount.result_entries() == []
    lines = stderr.splitlines()
    assert len(lines) == 1
    assert len(lines[0]) <= MAX_STDERR_LINE
    assert lines[0].endswith(f"failed {reason}")
    assert MEDIA_MARKER.decode() not in stderr


def test_job_without_segment_media_fails_with_segment_media_required(
    tmp_path: Path, mount_factory: Callable[..., Mount], capsys: pytest.CaptureFixture[str]
) -> None:
    mount = _mount(mount_factory, media_support.hard_cuts_clip(tmp_path).path, with_segment_media=False)
    shutil.rmtree(mount.media_folder)

    code, stderr = _run(mount, capsys)

    assert code == 2
    _assert_failed_with(mount, stderr, "segment-media-required")


def test_undecodable_media_fails_with_media_decode_failed(
    tmp_path: Path, mount_factory: Callable[..., Mount], capsys: pytest.CaptureFixture[str]
) -> None:
    broken = tmp_path / "broken.mp4"
    broken.write_bytes(MEDIA_MARKER * 100)
    mount = _mount(mount_factory, broken)

    code, stderr = _run(mount, capsys)

    assert code == 1
    _assert_failed_with(mount, stderr, "media-decode-failed")


def test_media_without_a_video_stream_fails_with_media_no_video_stream(
    tmp_path: Path, mount_factory: Callable[..., Mount], capsys: pytest.CaptureFixture[str]
) -> None:
    mount = _mount(mount_factory, media_support.create_audio_only_media(tmp_path))

    code, stderr = _run(mount, capsys)

    assert code == 2
    _assert_failed_with(mount, stderr, "media-no-video-stream")


def test_frame_in_the_requested_interval_outside_every_span_fails_with_timestamp_unmappable(
    tmp_path: Path, mount_factory: Callable[..., Mount], capsys: pytest.CaptureFixture[str]
) -> None:
    clip = media_support.hard_cuts_clip(tmp_path)
    mount = _mount(mount_factory, clip.path, spans=[{"mediaStartMs": 0, "mediaEndMs": 1000, "matchStartMs": 0}])

    code, stderr = _run(mount, capsys)

    assert code == 1
    _assert_failed_with(mount, stderr, "timestamp-unmappable")
