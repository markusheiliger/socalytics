"""Streaming hard-cut detector over decoded frames (research.md R-04, R-05).

Only the previous decoded frame and one pending boundary are kept. A pair is scored only when its second frame is in the
nominal window and the requested source interval; the previous frame is the predecessor even when it is a lead-in frame.
"""

from __future__ import annotations

from collections.abc import Iterable, Iterator
from dataclasses import dataclass
from decimal import Decimal

import numpy as np
from socalytics_analyst import ResultError
from socalytics_analyst.media import SegmentMedia

from .measure import change_measure
from .settings import HARD_THRESHOLD

HARD_CUT = "hard-cut"
TIMESTAMP_UNMAPPABLE = "timestamp-unmappable"
_HARD_THRESHOLD = Decimal(str(HARD_THRESHOLD))


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


def detect(
    media: SegmentMedia, window: tuple[int, int], frames: Iterable[tuple[int, np.ndarray]]
) -> Iterator[DetectedBoundary]:
    """Yield the boundaries in match-time order; `window` is the job's nominal window as `(startMs, endMs)`."""
    source = media.manifest.requested_source_interval
    previous: np.ndarray | None = None
    previous_match_ms: int | None = None
    pending: DetectedBoundary | None = None

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

        if in_window:
            assert match_ms is not None
            if pending is not None:
                yield _with_next(pending, match_ms)
                pending = None
            if previous is not None:
                measure = change_measure(previous, frame)
                if measure >= _HARD_THRESHOLD:
                    pending = DetectedBoundary(
                        frame_index=index,
                        start_ms=match_ms,
                        rule=HARD_CUT,
                        measure=measure,
                        context_mean=None,
                        previous_frame_ms=previous_match_ms,
                        next_frame_ms=None,
                    )
        previous, previous_match_ms = frame, match_ms

    if pending is not None:
        yield pending


def _with_next(boundary: DetectedBoundary, next_frame_ms: int) -> DetectedBoundary:
    return DetectedBoundary(**{**boundary.__dict__, "next_frame_ms": next_frame_ms})
