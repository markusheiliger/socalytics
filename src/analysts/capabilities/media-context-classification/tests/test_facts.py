from __future__ import annotations

import json
from collections.abc import Callable
from decimal import Decimal
from pathlib import Path

import pytest
from socalytics_analyst import run

from media_context_classification.__main__ import analyse
from media_context_classification.facts import fact_end_ms, shot_boundary_fact
from media_context_classification.settings import settings
from support import media as media_support
from support.mounts import Mount

SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]
PAYLOAD_PROPERTIES = {
    "rule", "changeMeasure", "hardThreshold", "frameIndex", "previousShotLastFrameMs", "settings",
}


def _mount(mount_factory: Callable[..., Mount], media: Path, window_end: int = 5000) -> Mount:
    window = {"startMs": 0, "endMs": window_end}
    return mount_factory(
        media,
        nominal_window=window,
        requested_source_interval=window,
        encoded_interval=window,
        timeline_spans=[{"mediaStartMs": 0, "mediaEndMs": window_end, "matchStartMs": 0}],
    )


def _run(mount: Mount) -> int:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0


def _facts(mount: Mount) -> list[dict]:
    return json.loads((mount.result / "facts.json").read_text(encoding="utf-8"))["facts"]


class _Boundary:
    def __init__(self, start_ms: int, rule: str = "hard-cut", context_mean: Decimal | None = None) -> None:
        self.frame_index = 3
        self.start_ms = start_ms
        self.rule = rule
        self.measure = Decimal("57.12349")
        self.context_mean = context_mean
        self.previous_frame_ms = start_ms - 40


def test_range_ends_at_the_next_in_window_frame_and_at_the_window_end_for_the_last_frame(tmp_path, mount_factory):
    clip = media_support.hard_cuts_clip(tmp_path)
    mount = _mount(mount_factory, clip.path)

    assert _run(mount) == 0
    facts = _facts(mount)
    assert [(f["startMs"], f["endMs"]) for f in facts] == [
        (clip.frame_ms(index), clip.frame_ms(index + 1)) for index in clip.cut_indexes
    ]

    last = media_support.create_clip(
        tmp_path, [media_support.Shot(49, media_support.BLACK), media_support.Shot(1, media_support.WHITE)], name="last.mp4"
    )
    last_mount = _mount(mount_factory, last.path, window_end=2000)
    assert _run(last_mount) == 0
    assert [(f["startMs"], f["endMs"]) for f in _facts(last_mount)] == [(last.frame_ms(49), 2000)]


def test_range_is_raised_to_start_plus_one_when_equal_and_never_beyond_the_window_end():
    assert fact_end_ms(1000, 1040, 5000) == 1040
    assert fact_end_ms(1000, 1000, 5000) == 1001
    assert fact_end_ms(1000, None, 5000) == 5000
    assert fact_end_ms(4999, 4999, 5000) == 5000
    assert fact_end_ms(4999, None, 5000) == 5000

    fact = shot_boundary_fact(_Boundary(2000), 2000, 5000)
    assert (fact.start_ms, fact.end_ms) == (2000, 2001)


def test_hard_cut_fact_has_confidence_and_the_contract_payload(tmp_path, mount_factory):
    clip = media_support.hard_cuts_clip(tmp_path)
    mount = _mount(mount_factory, clip.path)

    assert _run(mount) == 0
    facts = _facts(mount)
    assert len(facts) == 3
    for fact, index in zip(facts, clip.cut_indexes, strict=True):
        assert fact["kind"] == "shot-boundary"
        assert fact["confidence"] == 0.5
        payload = fact["payload"]
        assert set(payload) == PAYLOAD_PROPERTIES
        assert payload["rule"] == "hard-cut"
        assert payload["hardThreshold"] == 30.0
        assert 30.0 <= payload["changeMeasure"] <= 255
        assert payload["changeMeasure"] == round(payload["changeMeasure"], 3)
        assert payload["frameIndex"] == index
        assert payload["previousShotLastFrameMs"] == clip.frame_ms(index - 1)
        assert payload["settings"] == settings


def test_hard_cut_fact_rounds_the_measure_to_three_decimals_without_adaptive_properties():
    fact = shot_boundary_fact(_Boundary(2000), 2040, 5000)
    assert fact.payload["changeMeasure"] == 57.123
    assert set(fact.payload) == PAYLOAD_PROPERTIES
    assert fact.confidence == 0.5


def test_previous_shot_last_frame_is_null_for_a_lead_in_predecessor():
    boundary = _Boundary(2000)
    boundary.previous_frame_ms = None
    assert shot_boundary_fact(boundary, None, 5000).payload["previousShotLastFrameMs"] is None


def test_facts_are_in_start_order(tmp_path, mount_factory):
    mount = _mount(mount_factory, media_support.hard_cuts_clip(tmp_path).path)

    assert _run(mount) == 0
    starts = [fact["startMs"] for fact in _facts(mount)]
    assert starts == sorted(starts) and len(set(starts)) == len(starts)


def test_segment_without_a_boundary_is_an_accepted_result_with_zero_facts(tmp_path, mount_factory):
    mount = _mount(mount_factory, media_support.no_cut_clip(tmp_path).path)

    assert _run(mount) == 0
    assert mount.result_entries() == ["facts.json", "manifest.json"]
    assert _facts(mount) == []
