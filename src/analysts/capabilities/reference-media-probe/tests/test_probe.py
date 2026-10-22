from __future__ import annotations

from collections.abc import Callable
from decimal import Decimal
from pathlib import Path

import pytest
from socalytics_analyst import ResultError

from reference_media_probe.probe import decode_frames


def test_25_fps_media_yields_25_frames_per_second(media_factory: Callable[..., Path], tmp_path: Path) -> None:
    frames = list(decode_frames(media_factory(duration_seconds=2), tmp_path))

    assert len(frames) == 50
    times = [media_ms for media_ms, _ in frames]
    assert times == sorted(set(times))
    assert times[0] == 0
    assert sum(1 for t in times if t < 1000) == 25
    assert all(isinstance(y, Decimal) and 0 <= y <= 255 for _, y in frames)


def test_offset_start_keeps_source_timestamps(media_factory: Callable[..., Path], tmp_path: Path) -> None:
    frames = list(decode_frames(media_factory(duration_seconds=1, offset_seconds=10), tmp_path))

    assert len(frames) == 25
    assert frames[0][0] == 10000
    assert frames[-1][0] < 11000


def test_path_with_special_characters_is_decoded(media_factory: Callable[..., Path], tmp_path: Path) -> None:
    media = media_factory(duration_seconds=1, name="a b,c's[1];d:e.mp4")

    assert len(list(decode_frames(media, tmp_path))) == 25


def test_bytes_that_are_not_media_raise_media_decode_failed(tmp_path: Path) -> None:
    broken = tmp_path / "broken.mp4"
    broken.write_bytes(b"this is not media" * 100)

    with pytest.raises(ResultError) as raised:
        list(decode_frames(broken, tmp_path))

    assert raised.value.category == "media-decode-failed"


def test_missing_file_raises_media_decode_failed(tmp_path: Path) -> None:
    with pytest.raises(ResultError) as raised:
        list(decode_frames(tmp_path / "missing.mp4", tmp_path))

    assert raised.value.category == "media-decode-failed"
