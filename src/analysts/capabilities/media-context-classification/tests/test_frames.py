from __future__ import annotations

import inspect
from fractions import Fraction
from pathlib import Path

import numpy as np
import pytest
from socalytics_analyst import InputError, ResultError

from media_context_classification import frames
from media_context_classification.frames import decode_frames, read_time_base, read_timestamps
from support import media as media_support


def _decode(path: Path) -> list[tuple[int, np.ndarray]]:
    return [(ms, frame.copy()) for ms, frame in decode_frames(path, scratch=None)]


def test_timestamps_pair_with_frames_one_to_one(tmp_path):
    clip = media_support.hard_cuts_clip(tmp_path)
    decoded = _decode(clip.path)
    assert len(decoded) == clip.frame_count
    assert [ms for ms, _ in decoded] == [clip.frame_ms(index) for index in range(clip.frame_count)]
    assert [ms for ms, _ in decoded] == read_timestamps(clip.path, scratch=None)


def test_frames_are_fixed_size_bgr_and_follow_the_pixels_of_each_shot(tmp_path):
    clip = media_support.hard_cuts_clip(tmp_path)
    decoded = _decode(clip.path)
    for _, frame in decoded:
        assert frame.shape == (frames.FRAME_HEIGHT, frames.FRAME_WIDTH, 3)
        assert frame.dtype == np.uint8
    steps = [
        float(np.abs(decoded[index][1].astype(np.int16) - decoded[index - 1][1].astype(np.int16)).mean())
        for index in range(1, len(decoded))
    ]
    assert [index + 1 for index, step in enumerate(steps) if step > 20] == list(clip.cut_indexes)
    assert decoded[0][1].mean() < 40 and decoded[25][1].mean() > 200


def test_timestamp_offset_is_kept(tmp_path):
    clip = media_support.hard_cuts_clip(tmp_path, offset_seconds=12.0)
    timestamps = read_timestamps(clip.path, scratch=None)
    assert timestamps[0] == 12_000
    assert timestamps == [clip.frame_ms(index) for index in range(clip.frame_count)]


def test_timestamps_are_floored_from_the_exact_time_base(tmp_path):
    clip = media_support.hard_cuts_clip(tmp_path, frame_rate=30)
    time_base = read_time_base(clip.path, scratch=None)
    assert isinstance(time_base, Fraction) and time_base > 0
    timestamps = read_timestamps(clip.path, scratch=None)
    assert timestamps[:4] == [0, 33, 66, 100]


def test_single_frame_media_yields_one_pair(tmp_path):
    clip = media_support.single_frame_clip(tmp_path)
    decoded = _decode(clip.path)
    assert [ms for ms, _ in decoded] == [0]


def test_frames_stream_one_record_at_a_time(tmp_path, monkeypatch):
    clip = media_support.hard_cuts_clip(tmp_path)
    requested: list[int] = []

    original = frames._read_record

    def spy(stream: object) -> bytes | None:
        record = original(stream)
        requested.append(0 if record is None else len(record))
        return record

    monkeypatch.setattr(frames, "_read_record", spy)
    count = 0
    for _, frame in decode_frames(clip.path, scratch=None):
        assert not frame.flags.writeable
        count += 1
        assert len(requested) == count
    assert set(requested) == {frames.FRAME_BYTES, 0}
    assert inspect.isgeneratorfunction(decode_frames)


def test_stderr_buffer_is_bounded():
    class Noisy:
        def __init__(self) -> None:
            self.chunks = [b"x" * 1024] * 100

        def read(self, _: int) -> bytes:
            return self.chunks.pop() if self.chunks else b""

    text = frames._BoundedStderr(Noisy()).text()
    assert len(text) == frames.STDERR_LIMIT_BYTES


def test_audio_only_media_has_no_video_stream(tmp_path):
    media = media_support.create_audio_only_media(tmp_path)
    with pytest.raises(InputError) as error:
        list(decode_frames(media, scratch=None))
    assert error.value.category == "media-no-video-stream"


def test_undecodable_media_fails_with_media_decode_failed(tmp_path):
    media = tmp_path / "segment.mp4"
    media.write_bytes(b"this is not a media container")
    with pytest.raises(ResultError) as error:
        list(decode_frames(media, scratch=None))
    assert error.value.category == "media-decode-failed"


def test_missing_media_fails_with_media_decode_failed(tmp_path):
    with pytest.raises(ResultError) as error:
        list(decode_frames(tmp_path / "missing.mp4", scratch=None))
    assert error.value.category == "media-decode-failed"


def test_truncated_media_fails_with_media_decode_failed(tmp_path):
    clip = media_support.hard_cuts_clip(tmp_path)
    truncated = tmp_path / "truncated.mp4"
    truncated.write_bytes(clip.path.read_bytes()[:2000])
    with pytest.raises(ResultError) as error:
        list(decode_frames(truncated, scratch=None))
    assert error.value.category == "media-decode-failed"


def test_missing_tool_fails_with_media_decode_failed(tmp_path, monkeypatch):
    clip = media_support.single_frame_clip(tmp_path)
    monkeypatch.setenv("PATH", "")
    with pytest.raises(ResultError) as error:
        list(decode_frames(clip.path, scratch=None))
    assert error.value.category == "media-decode-failed"


def test_frame_count_mismatch_fails_with_media_decode_failed(tmp_path, monkeypatch):
    clip = media_support.hard_cuts_clip(tmp_path)
    real = frames.read_timestamps
    monkeypatch.setattr(frames, "read_timestamps", lambda media, scratch=None: real(media, scratch)[:-1])
    with pytest.raises(ResultError, match="more frames") as error:
        list(decode_frames(clip.path, scratch=None))
    assert error.value.category == "media-decode-failed"

    monkeypatch.setattr(frames, "read_timestamps", lambda media, scratch=None: [*real(media, scratch), 9999])
    with pytest.raises(ResultError, match="listed timestamps") as error:
        list(decode_frames(clip.path, scratch=None))
    assert error.value.category == "media-decode-failed"


def test_empty_video_stream_media_has_no_video_stream(tmp_path):
    media = media_support.create_empty_video_clip(tmp_path)
    with pytest.raises(InputError) as error:
        list(decode_frames(media, scratch=None))
    assert error.value.category == "media-no-video-stream"
