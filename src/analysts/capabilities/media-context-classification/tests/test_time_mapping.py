from __future__ import annotations

import json
from collections.abc import Callable
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
from socalytics_analyst import ResultError, run
from socalytics_analyst.media import SegmentMedia

from media_context_classification.__main__ import analyse
from media_context_classification.detector import TIMESTAMP_UNMAPPABLE, detect
from media_context_classification.facts import shot_boundary_fact
from support import media as media_support
from support.mounts import Mount

DARK = np.full((36, 64, 3), 16, dtype=np.uint8)
LIGHT = np.full((36, 64, 3), 235, dtype=np.uint8)


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


def _run(mount: Mount) -> int:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0


def test_facts_of_variable_frame_rate_media_derive_from_frame_timestamps() -> None:
    media = _media([(0, 2000, 7000)], source=(0, 2000))
    window = (7000, 9000)
    frames = [(0, DARK), (40, DARK), (100, DARK), (130, DARK), (400, LIGHT), (410, LIGHT), (1500, DARK)]

    boundaries = list(detect(media, window, frames))  # type: ignore[arg-type]

    assert [(b.frame_index, b.start_ms, b.previous_frame_ms, b.next_frame_ms) for b in boundaries] == [
        (4, 7400, 7130, 7410),
        (6, 8500, 7410, None),
    ]
    facts = [shot_boundary_fact(b, b.next_frame_ms, window[1]) for b in boundaries]
    assert [(f.start_ms, f.end_ms) for f in facts] == [(7400, 7410), (8500, 9000)]


def test_non_zero_segment_offset_maps_frame_timestamps_to_match_time(
    tmp_path: Path, mount_factory: Callable[..., Mount]
) -> None:
    clip = media_support.hard_cuts_clip(tmp_path, offset_seconds=2.0)
    mount = mount_factory(
        clip.path,
        nominal_window={"startMs": 10000, "endMs": 14000},
        requested_source_interval={"startMs": 2000, "endMs": 6000},
        encoded_interval={"startMs": 2000, "endMs": 6000},
        timeline_spans=[{"mediaStartMs": 2000, "mediaEndMs": 6000, "matchStartMs": 10000}],
    )

    assert _run(mount) == 0

    facts = json.loads((mount.result / "facts.json").read_text(encoding="utf-8"))["facts"]
    expected = [10000 + clip.frame_ms(index) - 2000 for index in clip.cut_indexes]
    assert expected == [11000, 12000, 13000]
    assert [fact["startMs"] for fact in facts] == expected
    assert [fact["endMs"] for fact in facts] == [start + 40 for start in expected]


def test_frame_in_the_requested_interval_that_cannot_be_mapped_fails_as_timestamp_unmappable() -> None:
    media = _media([(0, 1000, 0)], source=(0, 2000))
    frames = [(0, DARK), (500, DARK), (1500, LIGHT)]

    with pytest.raises(ResultError) as failure:
        list(detect(media, (0, 2000), frames))  # type: ignore[arg-type]

    assert failure.value.category == TIMESTAMP_UNMAPPABLE


def test_unmappable_frame_ends_the_attempt_as_failed_without_a_result(
    tmp_path: Path, mount_factory: Callable[..., Mount]
) -> None:
    clip = media_support.hard_cuts_clip(tmp_path)
    mount = mount_factory(
        clip.path,
        nominal_window={"startMs": 0, "endMs": 4000},
        requested_source_interval={"startMs": 0, "endMs": 4000},
        encoded_interval={"startMs": 0, "endMs": 4000},
        timeline_spans=[{"mediaStartMs": 0, "mediaEndMs": 2000, "matchStartMs": 0}],
    )

    assert _run(mount) == 1
    assert "facts.json" not in mount.result_entries()
