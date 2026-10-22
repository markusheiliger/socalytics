"""Aggregation of decoded frames into one fact per match-second, streaming and in constant memory per second."""

from __future__ import annotations

from collections.abc import Iterable
from decimal import ROUND_HALF_EVEN, Decimal

from socalytics_analyst import Fact
from socalytics_analyst.media import SegmentMedia

from . import FACT_KIND

SECOND_MS = 1000
_THOUSANDTH = Decimal("0.001")


class _Second:
    __slots__ = ("count", "low", "high", "total")

    def __init__(self) -> None:
        self.count = 0
        self.low = Decimal(0)
        self.high = Decimal(0)
        self.total = Decimal(0)

    def add(self, luma: Decimal) -> None:
        if self.count == 0:
            self.low = self.high = luma
        else:
            self.low, self.high = min(self.low, luma), max(self.high, luma)
        self.count += 1
        self.total += luma


def _round(value: Decimal) -> float:
    return float(value.quantize(_THOUSANDTH, rounding=ROUND_HALF_EVEN))


def _seconds(media: SegmentMedia, window: tuple[int, int]) -> dict[int, tuple[int, int]]:
    """Fact range per match-second that overlaps both the nominal window and the mapped match time."""
    ranges: dict[int, tuple[int, int]] = {}
    for span in media.manifest.timeline_spans:
        start = max(span.match_start_ms, window[0])
        end = min(span.match_start_ms + (span.media_end_ms - span.media_start_ms), window[1])
        if end <= start:
            continue
        for second in range(start // SECOND_MS, (end - 1) // SECOND_MS + 1):
            low, high = max(start, second * SECOND_MS), min(end, (second + 1) * SECOND_MS)
            known = ranges.get(second)
            ranges[second] = (low, high) if known is None else (min(known[0], low), max(known[1], high))
    return ranges


def frame_second_facts(
    media: SegmentMedia, frames: Iterable[tuple[int, Decimal]], window: tuple[int, int]
) -> list[Fact]:
    """Build the facts in start-time order; `window` is the job's nominal window as `(startMs, endMs)`."""
    source = media.manifest.requested_source_interval
    ranges = _seconds(media, window)
    seconds: dict[int, _Second] = {}
    for media_ms, luma in frames:
        if not source.start_ms <= media_ms < source.end_ms:
            continue
        match_ms = media.to_match_ms(media_ms)
        if match_ms is None or not window[0] <= match_ms < window[1]:
            continue
        seconds.setdefault(match_ms // SECOND_MS, _Second()).add(luma)

    facts: list[Fact] = []
    for second in sorted(ranges):
        start, end = ranges[second]
        aggregate = seconds.get(second)
        payload: dict[str, object] = {"decodedFrames": aggregate.count if aggregate else 0}
        if aggregate and aggregate.count > 0:
            payload["meanLuma"] = {
                "min": _round(aggregate.low),
                "mean": _round(aggregate.total / aggregate.count),
                "max": _round(aggregate.high),
            }
        facts.append(Fact(kind=FACT_KIND, start_ms=start, end_ms=end, confidence=1.0, payload=payload))
    return facts
