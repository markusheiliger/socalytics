from __future__ import annotations

import json
from collections.abc import Callable
from decimal import Decimal
from pathlib import Path
from types import SimpleNamespace

import pytest
from jsonschema import Draft202012Validator
from socalytics_analyst import run
from socalytics_analyst.media import SegmentMedia

from reference_media_probe.__main__ import analyse
from reference_media_probe.facts import frame_second_facts
from support.mounts import Mount

PAYLOAD_SCHEMA = json.loads(
    (Path(__file__).parent.parent / "schemas" / "frame-second-probe.payload.schema.json").read_text(encoding="utf-8")
)


def _media(spans: list[tuple[int, int, int]], source: tuple[int, int]) -> SimpleNamespace:
    """A stand-in for `SegmentMedia` with spans as `(mediaStartMs, mediaEndMs, matchStartMs)`."""
    stub = SimpleNamespace(
        manifest=SimpleNamespace(
            requested_source_interval=SimpleNamespace(start_ms=source[0], end_ms=source[1]),
            timeline_spans=[
                SimpleNamespace(media_start_ms=start, media_end_ms=end, match_start_ms=match) for start, end, match in spans
            ],
        )
    )
    stub.to_match_ms = lambda media_ms: SegmentMedia.to_match_ms(stub, media_ms)  # type: ignore[arg-type]
    return stub


def _frames(*times_ms: int) -> list[tuple[int, Decimal]]:
    return [(time, Decimal(100)) for time in times_ms]


def _ranges(facts) -> list[tuple[int, int]]:
    return [(fact.start_ms, fact.end_ms) for fact in facts]


def _counts(facts) -> list[int]:
    return [fact.payload["decodedFrames"] for fact in facts]


def _assert_inside(facts, window: tuple[int, int]) -> None:
    for fact in facts:
        assert window[0] <= fact.start_ms < fact.end_ms <= window[1]


def test_keyframe_context_before_the_requested_interval_yields_no_fact() -> None:
    media = _media([(1000, 4000, 0)], source=(1000, 4000))
    frames = _frames(0, 500, 1000, 1500, 2000, 2500, 3000, 3500)

    facts = frame_second_facts(media, frames, (0, 3000))

    assert _ranges(facts) == [(0, 1000), (1000, 2000), (2000, 3000)]
    assert _counts(facts) == [2, 2, 2]
    _assert_inside(facts, (0, 3000))


def test_nominal_window_partly_covered_clips_the_last_fact() -> None:
    media = _media([(0, 2500, 0)], source=(0, 2500))

    facts = frame_second_facts(media, _frames(0, 1000, 2000, 2400), (0, 5000))

    assert _ranges(facts) == [(0, 1000), (1000, 2000), (2000, 2500)]
    assert _counts(facts) == [1, 1, 2]
    _assert_inside(facts, (0, 5000))


def test_mapping_starting_after_the_window_start_yields_no_fact_before_it() -> None:
    media = _media([(0, 2000, 1500)], source=(0, 2000))

    facts = frame_second_facts(media, _frames(0, 500, 1000, 1500), (0, 5000))

    assert _ranges(facts) == [(1500, 2000), (2000, 3000), (3000, 3500)]
    assert _counts(facts) == [1, 2, 1]


def test_gap_between_spans_yields_no_fact_for_unmapped_seconds_and_clips_a_partly_mapped_second() -> None:
    media = _media([(0, 1500, 0), (1500, 3000, 3500)], source=(0, 3000))

    facts = frame_second_facts(media, _frames(0, 500, 1000, 1500, 2000, 2500), (0, 6000))

    # seconds 2 and the unmapped part of second 1 and 3 carry no fact; second 1 and 3 are clipped
    assert _ranges(facts) == [(0, 1000), (1000, 1500), (3500, 4000), (4000, 5000)]
    assert _counts(facts) == [2, 1, 1, 2]
    _assert_inside(facts, (0, 6000))


def test_two_spans_in_one_second_merge_into_one_range_clipped_to_the_mapped_extent() -> None:
    media = _media([(0, 300, 0), (300, 600, 600)], source=(0, 600))

    facts = frame_second_facts(media, _frames(0, 100, 300, 400), (0, 1000))

    assert _ranges(facts) == [(0, 900)]
    assert _counts(facts) == [4]


def test_frame_on_a_second_boundary_counts_in_the_later_second() -> None:
    media = _media([(0, 2000, 0)], source=(0, 2000))

    facts = frame_second_facts(media, _frames(999, 1000, 1999), (0, 2000))

    assert _counts(facts) == [1, 2]


def test_frame_at_the_windows_end_counts_in_no_second() -> None:
    media = _media([(0, 3000, 0)], source=(0, 3000))

    facts = frame_second_facts(media, _frames(0, 1999, 2000, 2500), (0, 2000))

    assert _ranges(facts) == [(0, 1000), (1000, 2000)]
    assert _counts(facts) == [1, 1]
    _assert_inside(facts, (0, 2000))


def test_frame_at_the_requested_source_end_counts_in_no_second() -> None:
    media = _media([(0, 3000, 0)], source=(0, 1000))

    facts = frame_second_facts(media, _frames(0, 999, 1000), (0, 2000))

    assert _counts(facts) == [2, 0]


def test_mapped_second_without_a_frame_yields_zero_frames_and_no_luma() -> None:
    media = _media([(0, 3000, 0)], source=(0, 3000))

    facts = frame_second_facts(media, _frames(0, 2000), (0, 3000))

    assert _counts(facts) == [1, 0, 1]
    assert "meanLuma" in facts[0].payload
    assert facts[1].payload == {"decodedFrames": 0}
    assert (facts[1].start_ms, facts[1].end_ms) == (1000, 2000)


def test_media_without_any_mapped_second_in_the_window_yields_no_fact() -> None:
    media = _media([(0, 1000, 10000)], source=(0, 1000))

    assert frame_second_facts(media, _frames(0, 500), (0, 5000)) == []


def _run(mount: Mount) -> int:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0


def _facts(mount: Mount) -> list[dict]:
    return json.loads((mount.result / "facts.json").read_text(encoding="utf-8"))["facts"]


def test_real_media_with_a_second_of_context_yields_no_fact_for_the_context(
    media_factory: Callable[..., Path], mount_factory: Callable[..., Mount]
) -> None:
    media = media_factory(duration_seconds=4)
    mount = mount_factory(
        media,
        nominal_window={"startMs": 0, "endMs": 3000},
        requested_source_interval={"startMs": 1000, "endMs": 4000},
        encoded_interval={"startMs": 0, "endMs": 4000},
        timeline_spans=[{"mediaStartMs": 1000, "mediaEndMs": 4000, "matchStartMs": 0}],
    )

    assert _run(mount) == 0
    facts = _facts(mount)
    assert [(f["startMs"], f["endMs"]) for f in facts] == [(0, 1000), (1000, 2000), (2000, 3000)]
    assert [f["payload"]["decodedFrames"] for f in facts] == [25, 25, 25]


def test_real_half_frame_rate_clip_yields_zero_frame_seconds(
    media_factory: Callable[..., Path], mount_factory: Callable[..., Mount]
) -> None:
    media = media_factory(duration_seconds=4, frame_rate=0.5)
    window = {"startMs": 0, "endMs": 4000}
    mount = mount_factory(
        media,
        nominal_window=window,
        requested_source_interval=window,
        encoded_interval=window,
        timeline_spans=[{"mediaStartMs": 0, "mediaEndMs": 4000, "matchStartMs": 0}],
    )

    assert _run(mount) == 0
    facts = _facts(mount)
    assert [(f["startMs"], f["endMs"]) for f in facts] == [(k * 1000, (k + 1) * 1000) for k in range(4)]
    assert [f["payload"]["decodedFrames"] for f in facts] == [1, 0, 1, 0]
    validator = Draft202012Validator(PAYLOAD_SCHEMA)
    for fact in facts:
        validator.validate(fact["payload"])
        assert ("meanLuma" in fact["payload"]) == (fact["payload"]["decodedFrames"] > 0)
