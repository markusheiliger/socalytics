from __future__ import annotations

from collections.abc import Callable
from decimal import Decimal
from fractions import Fraction

from socalytics_analyst.context import load_job
from socalytics_analyst.media import load_media

from media_context_classification.detector import ADAPTIVE_CUT, HARD_CUT, DetectedBoundary, detect
from media_context_classification.frames import decode_frames
from media_context_classification.measure import change_measure, quantize
from media_context_classification.settings import ADAPTIVE_FLOOR, ADAPTIVE_RATIO, ADAPTIVE_WINDOW, HARD_THRESHOLD
from support import media as media_support
from support.media import Shot
from support.mounts import Mount

GRAY_LOW = (100, 100, 100)
GRAY_HIGH = (130, 130, 130)


def _run(
    mount_factory: Callable[..., Mount], clip: media_support.Clip, window: tuple[int, int]
) -> tuple[list[DetectedBoundary], list[Decimal]]:
    """Detect on a clip staged as a job's media; also return the measure of every consecutive frame pair."""
    last_ms = clip.frame_ms(clip.frame_count - 1) + 1000
    span = (clip.frame_ms(0), last_ms)
    mount = mount_factory(
        clip.path,
        nominal_window={"startMs": window[0], "endMs": window[1]},
        requested_source_interval={"startMs": window[0], "endMs": window[1]},
        encoded_interval={"startMs": span[0], "endMs": span[1]},
        timeline_spans=[{"mediaStartMs": span[0], "mediaEndMs": span[1], "matchStartMs": span[0]}],
    )
    media = load_media(mount.root, load_job(mount.root))
    assert media is not None
    boundaries = list(detect(media, window, decode_frames(media.path, scratch=None)))
    frames = [frame for _, frame in decode_frames(media.path, scratch=None)]
    return boundaries, [change_measure(a, b) for a, b in zip(frames, frames[1:])]


def _mean(measures: list[Decimal]) -> Decimal:
    return quantize(sum((Fraction(m) for m in measures), Fraction(0)) / len(measures))


def test_static_scene_with_sensor_noise_yields_no_fact(tmp_path, mount_factory):
    clip = media_support.static_noise_clip(tmp_path)
    boundaries, measures = _run(mount_factory, clip, (0, 5000))
    assert boundaries == []
    assert max(measures) < HARD_THRESHOLD


def test_steady_pan_yields_no_fact(tmp_path, mount_factory):
    clip = media_support.steady_pan_clip(tmp_path)
    boundaries, _ = _run(mount_factory, clip, (0, 5000))
    assert boundaries == []


def test_low_contrast_cut_below_the_fixed_threshold_yields_one_adaptive_fact(tmp_path, mount_factory):
    clip = media_support.create_clip(
        tmp_path, [Shot(25, GRAY_LOW, noise=1.0), Shot(25, GRAY_HIGH, noise=1.0)]
    )
    boundaries, measures = _run(mount_factory, clip, (0, 5000))
    cut = clip.cut_indexes[0]
    assert ADAPTIVE_FLOOR <= measures[cut - 1] < HARD_THRESHOLD
    assert [(b.frame_index, b.rule, b.start_ms) for b in boundaries] == [(cut, ADAPTIVE_CUT, clip.frame_ms(cut))]
    boundary = boundaries[0]
    assert boundary.measure == measures[cut - 1]
    assert boundary.context_mean is not None
    assert boundary.context_mean == _mean(measures[cut - 1 - ADAPTIVE_WINDOW : cut - 1] + measures[cut : cut + ADAPTIVE_WINDOW])
    assert boundary.measure >= Fraction(str(ADAPTIVE_RATIO)) * Fraction(boundary.context_mean)


def test_very_high_contrast_cut_yields_one_hard_fact_and_no_adaptive_duplicate(tmp_path, mount_factory):
    clip = media_support.create_clip(tmp_path, [Shot(25, media_support.BLACK), Shot(25, media_support.WHITE)])
    boundaries, measures = _run(mount_factory, clip, (0, 5000))
    cut = clip.cut_indexes[0]
    assert measures[cut - 1] >= HARD_THRESHOLD
    assert measures[cut - 1] >= Fraction(str(ADAPTIVE_RATIO)) * Fraction(ADAPTIVE_FLOOR)
    assert [(b.frame_index, b.rule, b.context_mean) for b in boundaries] == [(cut, HARD_CUT, None)]


def test_adaptive_context_excludes_the_pair_itself(tmp_path, mount_factory):
    clip = media_support.create_clip(
        tmp_path, [Shot(25, GRAY_LOW, noise=1.0), Shot(25, GRAY_HIGH, noise=1.0)]
    )
    boundaries, measures = _run(mount_factory, clip, (0, 5000))
    cut = clip.cut_indexes[0]
    including_pair = _mean(measures[cut - 1 - ADAPTIVE_WINDOW : cut + ADAPTIVE_WINDOW])
    assert boundaries[0].context_mean != including_pair
    assert boundaries[0].context_mean < including_pair


def test_adaptive_context_excludes_pairs_before_the_window_start(tmp_path, mount_factory):
    clip = media_support.create_clip(
        tmp_path,
        [Shot(12, GRAY_LOW, noise=6.0), Shot(25, GRAY_HIGH, noise=0.5)],
    )
    cut = clip.cut_indexes[0]
    boundaries, measures = _run(mount_factory, clip, (clip.frame_ms(cut), 5000))
    assert [(b.frame_index, b.rule) for b in boundaries] == [(cut, ADAPTIVE_CUT)]
    # Only the pairs after the cut are in the window; the noisy pairs before it must not enter the context.
    assert boundaries[0].context_mean == _mean(measures[cut : cut + ADAPTIVE_WINDOW])
    assert boundaries[0].context_mean < _mean(measures[cut - ADAPTIVE_WINDOW : cut - 1])


def test_adaptive_context_excludes_pairs_after_the_window_end(tmp_path, mount_factory):
    clip = media_support.create_clip(
        tmp_path,
        [Shot(20, GRAY_LOW, noise=0.5), Shot(2, GRAY_HIGH, noise=0.5), Shot(20, GRAY_HIGH, noise=8.0)],
    )
    cut = clip.cut_indexes[0]
    end = clip.frame_ms(cut + 2)
    boundaries, measures = _run(mount_factory, clip, (0, end))
    assert [(b.frame_index, b.rule) for b in boundaries] == [(cut, ADAPTIVE_CUT)]
    # The pairs ending at frames cut + 2 and later lie outside the window; only the pair ending at cut + 1 follows the cut.
    expected = _mean(measures[cut - 1 - ADAPTIVE_WINDOW : cut - 1] + measures[cut : cut + 1])
    assert boundaries[0].context_mean == expected
