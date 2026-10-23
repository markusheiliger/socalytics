"""Container entry point: `python -m reference_media_probe`."""

from __future__ import annotations

from socalytics_analyst import AnalystContext, InputError, run

from .facts import frame_second_facts
from .probe import decode_frames

SEGMENT_MEDIA_REQUIRED = "segment-media-required"


def analyse(context: AnalystContext) -> None:
    if context.media is None:
        raise InputError(SEGMENT_MEDIA_REQUIRED, "the capability requires staged segment media.")
    window = context.job.segment_window
    facts = frame_second_facts(
        context.media, decode_frames(context.media.path, context.scratch), (window.start_ms, window.end_ms)
    )
    for fact in facts:
        context.result.add_fact(fact)
    if facts:
        context.result.set_confidence(1.0)


def main() -> None:
    run(analyse, output_type="foundational-facts")


if __name__ == "__main__":
    main()
