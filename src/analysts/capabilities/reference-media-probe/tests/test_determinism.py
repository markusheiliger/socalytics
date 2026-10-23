from __future__ import annotations

from collections.abc import Callable
from decimal import Decimal
from pathlib import Path
from types import SimpleNamespace

import pytest
from socalytics_analyst import run

from reference_media_probe.__main__ import analyse
from reference_media_probe.facts import frame_second_facts
from support.mounts import Mount

WINDOW = {"startMs": 0, "endMs": 5000}
SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]


def _run(mount: Mount) -> int:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0


def _identity_media() -> SimpleNamespace:
    interval = SimpleNamespace(start_ms=0, end_ms=2000)
    span = SimpleNamespace(media_start_ms=0, media_end_ms=2000, match_start_ms=0)
    manifest = SimpleNamespace(requested_source_interval=interval, timeline_spans=[span])
    return SimpleNamespace(manifest=manifest, to_match_ms=lambda media_ms: media_ms)


def _payloads(lumas: list[str]) -> list[dict]:
    frames = [(index * 100, Decimal(luma)) for index, luma in enumerate(lumas)]
    return [fact.payload for fact in frame_second_facts(_identity_media(), frames, (0, 1000))]  # type: ignore[arg-type]


def test_two_runs_in_two_mount_roots_yield_byte_identical_facts(
    media_factory: Callable[..., Path], mount_factory: Callable[..., Mount]
) -> None:
    media = media_factory()
    mounts = [
        mount_factory(
            media,
            nominal_window=WINDOW,
            requested_source_interval=WINDOW,
            encoded_interval=WINDOW,
            timeline_spans=SPANS,
        )
        for _ in range(2)
    ]

    assert [_run(mount) for mount in mounts] == [0, 0]

    first, second = (mount.result / "facts.json" for mount in mounts)
    assert mounts[0].root != mounts[1].root
    assert first.read_bytes() == second.read_bytes()


def test_luma_differing_beyond_the_third_decimal_yields_the_same_payload() -> None:
    assert _payloads(["100.0001", "120.0002", "110.0004"]) == _payloads(["100.0004", "120.0001", "110.0002"])


def test_luma_rounds_half_even_to_three_decimals() -> None:
    low = _payloads(["10.0005", "10.0005"])[0]["meanLuma"]
    high = _payloads(["10.0015", "10.0015"])[0]["meanLuma"]

    assert low == {"min": 10.0, "mean": 10.0, "max": 10.0}
    assert high == {"min": 10.002, "mean": 10.002, "max": 10.002}
