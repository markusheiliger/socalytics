from __future__ import annotations

from collections.abc import Callable
from pathlib import Path

from socalytics_analyst.context import load_job
from socalytics_analyst.media import load_media

from media_context_classification.detector import HARD_CUT, DetectedBoundary, detect
from media_context_classification.frames import decode_frames
from support import media as media_support
from support.mounts import Mount


def _detect(
    mount_factory: Callable[..., Mount],
    clip: media_support.Clip,
    *,
    window: tuple[int, int],
    source: tuple[int, int] | None = None,
    media_span: tuple[int, int] | None = None,
    match_start: int = 0,
) -> list[DetectedBoundary]:
    """Run the detector on a clip as the staged media of a job.

    `media_span` is the mapped media interval and `match_start` the match time of its start; the nominal window and the
    requested source interval default to the window (the source in media time).
    """
    last_ms = clip.frame_ms(clip.frame_count - 1) + 1000
    span_start, span_end = media_span if media_span is not None else (clip.frame_ms(0), last_ms)
    source_start, source_end = source if source is not None else (window[0] - match_start + span_start, window[1] - match_start + span_start)
    mount = mount_factory(
        clip.path,
        nominal_window={"startMs": window[0], "endMs": window[1]},
        requested_source_interval={"startMs": source_start, "endMs": source_end},
        encoded_interval={"startMs": span_start, "endMs": span_end},
        timeline_spans=[{"mediaStartMs": span_start, "mediaEndMs": span_end, "matchStartMs": match_start}],
    )
    media = load_media(mount.root, load_job(mount.root))
    assert media is not None
    return list(detect(media, window, decode_frames(media.path, scratch=None)))


def test_three_hard_cuts_are_reported_in_order(tmp_path, mount_factory):
    clip = media_support.hard_cuts_clip(tmp_path)
    boundaries = _detect(mount_factory, clip, window=(0, 5000))
    assert [b.frame_index for b in boundaries] == list(clip.cut_indexes)
    assert [b.start_ms for b in boundaries] == [clip.frame_ms(i) for i in clip.cut_indexes]
    assert [b.next_frame_ms for b in boundaries] == [clip.frame_ms(i + 1) for i in clip.cut_indexes]
    assert [b.previous_frame_ms for b in boundaries] == [clip.frame_ms(i - 1) for i in clip.cut_indexes]
    assert all(b.rule == HARD_CUT and b.context_mean is None and b.measure >= 30 for b in boundaries)


def test_cut_on_the_second_frame_of_the_segment_is_reported(tmp_path, mount_factory):
    clip = media_support.second_frame_cut_clip(tmp_path)
    boundaries = _detect(mount_factory, clip, window=(0, 5000))
    assert [(b.frame_index, b.start_ms, b.previous_frame_ms) for b in boundaries] == [(1, clip.frame_ms(1), clip.frame_ms(0))]


def test_no_cut_yields_no_boundary(tmp_path, mount_factory):
    clip = media_support.no_cut_clip(tmp_path)
    assert _detect(mount_factory, clip, window=(0, 5000)) == []


def test_fewer_than_two_frames_yield_no_boundary(tmp_path, mount_factory):
    clip = media_support.single_frame_clip(tmp_path)
    assert _detect(mount_factory, clip, window=(0, 5000)) == []


def test_cut_among_lead_in_frames_is_not_reported(tmp_path, mount_factory):
    clip = media_support.lead_in_clip(tmp_path, cut_in_lead_in=True)
    start = clip.frame_ms(clip.lead_in_frames)
    assert clip.cut_indexes == (2,)
    assert _detect(mount_factory, clip, window=(start, 5000)) == []


def test_cut_between_last_lead_in_frame_and_first_in_window_frame_is_reported(tmp_path, mount_factory):
    clip = media_support.lead_in_clip(tmp_path)
    first = clip.lead_in_frames
    boundaries = _detect(mount_factory, clip, window=(clip.frame_ms(first), 5000))
    assert [(b.frame_index, b.start_ms, b.previous_frame_ms) for b in boundaries] == [
        (first, clip.frame_ms(first), clip.frame_ms(first - 1))
    ]
    assert clip.cut_indexes == (first,)


def test_cut_across_the_segment_edge_with_a_lead_in_predecessor_is_reported_once_by_the_next_segment(tmp_path, mount_factory):
    clip = media_support.create_clip(
        tmp_path,
        [media_support.Shot(25, media_support.BLACK), media_support.Shot(25, media_support.WHITE)],
    )
    edge = clip.frame_ms(clip.cut_indexes[0])
    earlier = _detect(mount_factory, clip, window=(0, edge))
    later = _detect(mount_factory, clip, window=(edge, 2 * edge))
    assert earlier == []
    assert [(b.frame_index, b.start_ms, b.previous_frame_ms) for b in later] == [
        (clip.cut_indexes[0], edge, clip.frame_ms(clip.cut_indexes[0] - 1))
    ]


def test_cut_across_the_segment_edge_without_a_predecessor_is_reported_by_no_segment(tmp_path, mount_factory):
    before = media_support.create_clip(tmp_path, [media_support.Shot(25, media_support.BLACK)], name="before.mp4")
    edge = before.frame_ms(before.frame_count)
    after = media_support.create_clip(
        tmp_path, [media_support.Shot(25, media_support.WHITE)], name="after.mp4", offset_seconds=edge / 1000
    )
    earlier = _detect(mount_factory, before, window=(0, edge))
    later = _detect(mount_factory, after, window=(edge, 2 * edge), media_span=(edge, 2 * edge), match_start=edge)
    assert earlier == []
    assert later == []


def test_detector_keeps_no_state_between_runs(tmp_path: Path, mount_factory):
    clip = media_support.hard_cuts_clip(tmp_path)
    first = _detect(mount_factory, clip, window=(0, 5000))
    second = _detect(mount_factory, clip, window=(0, 5000))
    assert first == second
