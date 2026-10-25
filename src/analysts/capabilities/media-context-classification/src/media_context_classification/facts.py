"""Conversion of a detected shot boundary into a `shot-boundary` fact (contracts/shot-boundary-fact.md)."""

from __future__ import annotations

from decimal import ROUND_HALF_EVEN, Decimal
from typing import Protocol

from socalytics_analyst import Fact

from . import FACT_KIND
from .settings import ADAPTIVE_FLOOR, ADAPTIVE_RATIO, CONFIDENCE, HARD_THRESHOLD, settings

ADAPTIVE_CUT = "adaptive-cut"
_THOUSANDTH = Decimal("0.001")


class Boundary(Protocol):
    """Detector output for one boundary (data-model.md, "Shot boundary")."""

    frame_index: int
    start_ms: int
    rule: str
    measure: Decimal
    context_mean: Decimal | None
    previous_frame_ms: int | None


def _round(value: Decimal) -> float:
    return float(value.quantize(_THOUSANDTH, rounding=ROUND_HALF_EVEN))


def fact_end_ms(start_ms: int, next_frame_ms: int | None, window_end_ms: int) -> int:
    """End of a boundary's range: the next in-window frame (the window end for the last frame), above `start_ms`, within the window."""
    end_ms = window_end_ms if next_frame_ms is None else next_frame_ms
    return min(max(end_ms, start_ms + 1), window_end_ms)


def shot_boundary_fact(boundary: Boundary, next_frame_ms: int | None, window_end_ms: int) -> Fact:
    """Build the fact of one boundary; `next_frame_ms` is the match time of the next in-window frame, or `None` for the last one."""
    payload: dict[str, object] = {
        "rule": boundary.rule,
        "changeMeasure": _round(boundary.measure),
        "hardThreshold": HARD_THRESHOLD,
        "frameIndex": boundary.frame_index,
        "previousShotLastFrameMs": boundary.previous_frame_ms,
        "settings": dict(settings),
    }
    if boundary.rule == ADAPTIVE_CUT:
        if boundary.context_mean is None:
            raise ValueError("an adaptive-cut boundary requires a context mean.")
        payload["adaptiveFloor"] = ADAPTIVE_FLOOR
        payload["adaptiveRatio"] = ADAPTIVE_RATIO
        payload["contextMean"] = _round(boundary.context_mean)
    return Fact(
        kind=FACT_KIND,
        start_ms=boundary.start_ms,
        end_ms=fact_end_ms(boundary.start_ms, next_frame_ms, window_end_ms),
        confidence=CONFIDENCE,
        payload=payload,
    )
