"""Minimal Analyst: writes one fact for the job's window."""

from socalytics_analyst import AnalystContext, Fact, run


def analyse(context: AnalystContext) -> None:
    start, end = (0, 1000)
    if context.job.segment_window is not None:
        start, end = context.job.segment_window.start_ms, context.job.segment_window.end_ms
    context.result.add_fact(Fact(kind="example-seen", start_ms=start, end_ms=end, confidence=1.0, payload={}))
    context.result.set_confidence(1.0)
    if context.job.execution_scope == "match":
        context.result.set_validity_window(start, end)


if __name__ == "__main__":
    run(analyse, output_type="foundational-facts")
