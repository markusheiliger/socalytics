"""Streaming hard and adaptive cut detector over decoded frames (research.md R-04, R-05).

Only the previous decoded frame and a window of scored pairs (at most `ADAPTIVE_WINDOW` evaluated pairs, the pair under
evaluation, and `ADAPTIVE_WINDOW` pairs of lookahead) are kept. A pair is scored only when its second frame is in the
nominal window and the requested source interval; the previous frame is the predecessor even when it is a lead-in frame.
"""

from __future__ import annotations

from collections import deque
from collections.abc import Iterable, Iterator
from dataclasses import dataclass
from decimal import Decimal
from fractions import Fraction

import numpy as np
from socalytics_analyst import ResultError
from socalytics_analyst.media import SegmentMedia

from .measure import change_measure, quantize
from .settings import ADAPTIVE_FLOOR, ADAPTIVE_RATIO, ADAPTIVE_WINDOW, HARD_THRESHOLD, MIN_SHOT_LENGTH_MS

HARD_CUT = "hard-cut"
ADAPTIVE_CUT = "adaptive-cut"
TIMESTAMP_UNMAPPABLE = "timestamp-unmappable"
_HARD_THRESHOLD = Decimal(str(HARD_THRESHOLD))
_ADAPTIVE_FLOOR = Decimal(str(ADAPTIVE_FLOOR))
_ADAPTIVE_RATIO = Fraction(str(ADAPTIVE_RATIO))


@dataclass(frozen=True)
class DetectedBoundary:
    """A boundary (data-model.md, "Shot boundary") with the match time of the next in-window frame (`None` for the last)."""

    frame_index: int
    start_ms: int
    rule: str
    measure: Decimal
    context_mean: Decimal | None
    previous_frame_ms: int | None
    next_frame_ms: int | None


@dataclass(frozen=True)
class _Pair:
    frame_index: int
    match_ms: int
    previous_match_ms: int | None
    measure: Decimal


def detect(
    media: SegmentMedia, window: tuple[int, int], frames: Iterable[tuple[int, np.ndarray]]
) -> Iterator[DetectedBoundary]:
    """Yield the boundaries in match-time order; `window` is the job's nominal window as `(startMs, endMs)`."""
    source = media.manifest.requested_source_interval
    previous: np.ndarray | None = None
    previous_match_ms: int | None = None
    history: deque[_Pair] = deque(maxlen=ADAPTIVE_WINDOW)
    queue: deque[_Pair] = deque()
    last_start_ms: int | None = None

    def evaluate() -> Iterator[DetectedBoundary]:
        nonlocal last_start_ms
        pair = queue.popleft()
        boundary = _classify(pair, history, queue)
        history.append(pair)
        if boundary is None:
            return
        if last_start_ms is not None and boundary.start_ms - last_start_ms < MIN_SHOT_LENGTH_MS:
            return
        last_start_ms = boundary.start_ms
        yield boundary

    for index, (media_ms, frame) in enumerate(frames):
        match_ms: int | None = None
        in_window = False
        if source.start_ms <= media_ms < source.end_ms:
            match_ms = media.to_match_ms(media_ms)
            if match_ms is None:
                raise ResultError(TIMESTAMP_UNMAPPABLE, f"frame {index} at media time {media_ms} ms cannot be mapped.")
            in_window = window[0] <= match_ms < window[1]
        elif (mapped := media.to_match_ms(media_ms)) is not None:
            match_ms = mapped

        if in_window and previous is not None:
            assert match_ms is not None
            queue.append(_Pair(index, match_ms, previous_match_ms, change_measure(previous, frame)))
            if len(queue) > ADAPTIVE_WINDOW:
                yield from evaluate()
        previous, previous_match_ms = frame, match_ms

    while queue:
        yield from evaluate()


def _classify(pair: _Pair, history: deque[_Pair], queue: deque[_Pair]) -> DetectedBoundary | None:
    """Apply the hard rule first, then the adaptive rule; the context excludes the pair itself."""
    following = queue[0].match_ms if queue else None
    if pair.measure >= _HARD_THRESHOLD:
        return _boundary(pair, HARD_CUT, None, following)
    surrounding = [p.measure for p in history] + [p.measure for p in list(queue)[:ADAPTIVE_WINDOW]]
    if pair.measure < _ADAPTIVE_FLOOR or not surrounding:
        return None
    mean = sum((Fraction(m) for m in surrounding), Fraction(0)) / len(surrounding)
    if Fraction(pair.measure) >= _ADAPTIVE_RATIO * mean:
        return _boundary(pair, ADAPTIVE_CUT, quantize(mean), following)
    return None


def _boundary(pair: _Pair, rule: str, context_mean: Decimal | None, next_frame_ms: int | None) -> DetectedBoundary:
    return DetectedBoundary(
        frame_index=pair.frame_index,
        start_ms=pair.match_ms,
        rule=rule,
        measure=pair.measure,
        context_mean=context_mean,
        previous_frame_ms=pair.previous_match_ms,
        next_frame_ms=next_frame_ms,
    )
