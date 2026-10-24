from __future__ import annotations

from collections.abc import Callable

from media_context_classification.detector import HARD_CUT, DetectedBoundary, detect
from media_context_classification.frames import decode_frames
from media_context_classification.settings import MIN_SHOT_LENGTH_MS
from socalytics_analyst.context import load_job
from socalytics_analyst.media import load_media
from support import media as media_support
from support.mounts import Mount

WINDOW = (0, 5000)


def _detect(mount_factory: Callable[..., Mount], clip: media_support.Clip) -> list[DetectedBoundary]:
    last_ms = clip.frame_ms(clip.frame_count - 1) + 1000
    mount = mount_factory(
        clip.path,
        nominal_window={"startMs": WINDOW[0], "endMs": WINDOW[1]},
        requested_source_interval={"startMs": WINDOW[0], "endMs": WINDOW[1]},
        encoded_interval={"startMs": 0, "endMs": last_ms},
        timeline_spans=[{"mediaStartMs": 0, "mediaEndMs": last_ms, "matchStartMs": 0}],
    )
    media = load_media(mount.root, load_job(mount.root))
    assert media is not None
    return list(detect(media, WINDOW, decode_frames(media.path, scratch=None)))


def test_second_of_two_cuts_closer_than_the_minimum_leaves_no_trace(tmp_path, mount_factory):
    clip = media_support.closely_spaced_cuts_clip(tmp_path)
    first, second = clip.cut_indexes
    assert clip.frame_ms(second) - clip.frame_ms(first) < MIN_SHOT_LENGTH_MS

    boundaries = _detect(mount_factory, clip)

    assert [(b.frame_index, b.start_ms, b.rule) for b in boundaries] == [(first, clip.frame_ms(first), HARD_CUT)]
    # The reported boundary describes its own pair only; the suppressed cut does not change it.
    assert boundaries[0].next_frame_ms == clip.frame_ms(first + 1)
    assert boundaries[0].previous_frame_ms == clip.frame_ms(first - 1)


def test_suppressed_boundary_does_not_move_the_reference_point(tmp_path, mount_factory):
    clip = media_support.create_clip(
        tmp_path,
        [
            media_support.Shot(20, media_support.BLACK),
            media_support.Shot(5, media_support.WHITE),
            media_support.Shot(10, media_support.RED),
            media_support.Shot(20, media_support.BLUE),
        ],
    )
    first, suppressed, third = clip.cut_indexes
    assert clip.frame_ms(suppressed) - clip.frame_ms(first) < MIN_SHOT_LENGTH_MS
    assert clip.frame_ms(third) - clip.frame_ms(first) >= MIN_SHOT_LENGTH_MS
    assert clip.frame_ms(third) - clip.frame_ms(suppressed) < MIN_SHOT_LENGTH_MS

    boundaries = _detect(mount_factory, clip)

    assert [b.frame_index for b in boundaries] == [first, third]
    assert [b.start_ms for b in boundaries] == [clip.frame_ms(first), clip.frame_ms(third)]


def test_first_boundary_of_a_segment_is_never_suppressed(tmp_path, mount_factory):
    clip = media_support.create_clip(
        tmp_path,
        [media_support.Shot(1, media_support.BLACK), media_support.Shot(2, media_support.WHITE), media_support.Shot(30, media_support.RED)],
    )
    first, second = clip.cut_indexes
    assert clip.frame_ms(first) < MIN_SHOT_LENGTH_MS
    assert clip.frame_ms(second) - clip.frame_ms(first) < MIN_SHOT_LENGTH_MS

    boundaries = _detect(mount_factory, clip)

    assert [(b.frame_index, b.start_ms) for b in boundaries] == [(first, clip.frame_ms(first))]
