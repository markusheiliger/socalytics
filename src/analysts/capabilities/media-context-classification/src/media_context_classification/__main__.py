"""Container entry point: `python -m media_context_classification`.

Reads only the job document and the staged segment media; a failure raises before anything is written, so no result exists.
"""

from __future__ import annotations

from socalytics_analyst import AnalystContext, InputError, run

from .detector import detect
from .facts import shot_boundary_fact
from .frames import decode_frames
from .settings import CONFIDENCE

SEGMENT_MEDIA_REQUIRED = "segment-media-required"


def analyse(context: AnalystContext) -> None:
    if context.media is None:
        raise InputError(SEGMENT_MEDIA_REQUIRED, "the capability requires staged segment media.")
    window = context.job.segment_window
    boundaries = detect(context.media, (window.start_ms, window.end_ms), decode_frames(context.media.path, context.scratch))
    added = False
    for boundary in boundaries:
        context.result.add_fact(shot_boundary_fact(boundary, boundary.next_frame_ms, window.end_ms))
        added = True
    if added:
        context.result.set_confidence(CONFIDENCE)


def main() -> None:
    run(analyse, output_type="foundational-facts")


if __name__ == "__main__":
    main()
