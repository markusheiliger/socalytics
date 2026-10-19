"""Entry point of an SDK-built Analyst: load, call, finalize, and report through the exit status."""

from __future__ import annotations

import sys
from collections.abc import Callable
from pathlib import Path
from typing import NoReturn

from . import context as context_module
from . import errors
from .result import ResultWriter

SUCCESS_EXIT_CODE = 0
_MAX_FAILURE_LINE = 200


def _failure_line(category: str) -> str:
    return f"socalytics-analyst: failed {category}"[:_MAX_FAILURE_LINE]


def run(
    analyst: Callable[[context_module.AnalystContext], object],
    *,
    output_type: str,
    root: Path = context_module.DEFAULT_ROOT,
) -> NoReturn:
    """Run `analyst` against the mount `root` and exit; `root` exists only for tests.

    Exits 0 after a finalized result, 2 for input errors, and 1 for every other failure. A failure removes the
    staging directory and anything the writer placed, and reports only the error category on standard error.
    """
    root = Path(root)
    writer: ResultWriter | None = None
    try:
        job, attempt = context_module.load_documents(root)
        writer = ResultWriter(job, attempt, output_type, root)
        analyst(context_module.build_context(root, job, attempt, writer))
        writer.finalize()
        summary = f"socalytics-analyst: finalized facts={writer.fact_count} artifacts={writer.artifact_count}"
    except Exception as exc:
        if writer is not None:
            writer.discard()
        if isinstance(exc, errors.AnalystSdkError):
            category, code = exc.category, exc.exit_code
        else:
            category, code = errors.ANALYST_FAILED, errors.RESULT_EXIT_CODE
        print(_failure_line(category), file=sys.stderr)
        sys.exit(code)
    print(summary)
    sys.exit(SUCCESS_EXIT_CODE)
