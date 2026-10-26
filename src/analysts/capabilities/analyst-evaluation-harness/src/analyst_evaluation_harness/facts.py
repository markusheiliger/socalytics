"""Read the facts of one execution and classify failed executions (research R8)."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

from pydantic import ValidationError
from socalytics_analyst.models.facts import Fact, FactArtifact

FACTS_FILE_NAME = "facts.json"

TIMEOUT = "timeout"
INVALID_RESULT = "invalid-result"
EXECUTION_FAILED = "execution-failed"

_FAILURE_LINE = re.compile(r"^socalytics-analyst: failed (\S+)\s*$")


@dataclass(frozen=True)
class FactsOutcome:
    """Validated facts, or the failure category `invalid-result` with a short reason."""

    facts: tuple[Fact, ...] | None
    category: str | None = None
    reason: str | None = None

    @property
    def valid(self) -> bool:
        return self.facts is not None


def _invalid(reason: str) -> FactsOutcome:
    return FactsOutcome(facts=None, category=INVALID_RESULT, reason=reason)


def read_facts(result_dir: Path) -> FactsOutcome:
    """Validate `<result_dir>/facts.json` with the SDK fact-artifact model.

    Never raises for a missing, unreadable, or invalid file; the reason names the problem without echoing content.
    """
    path = Path(result_dir) / FACTS_FILE_NAME
    try:
        raw = path.read_bytes()
    except FileNotFoundError:
        return _invalid("facts.json is missing")
    except OSError:
        return _invalid("facts.json is unreadable")
    try:
        document = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, ValueError):
        return _invalid("facts.json is not valid JSON")
    try:
        artifact = FactArtifact.model_validate(document)
    except ValidationError as exc:
        locations = sorted({".".join(str(part) for part in error["loc"]) for error in exc.errors()})
        return _invalid("facts.json does not match the fact artifact: " + ", ".join(locations[:5]))
    for fact in artifact.facts:
        if fact.end_ms <= fact.start_ms:
            return _invalid("a fact ends before it starts")
    return FactsOutcome(facts=tuple(artifact.facts))


def sdk_failure_category(stderr: str) -> str | None:
    """Category of the last `socalytics-analyst: failed <category>` line on standard error, if any."""
    category: str | None = None
    for line in stderr.splitlines():
        match = _FAILURE_LINE.match(line.strip())
        if match:
            category = match.group(1)
    return category


def classify_failure(*, timed_out: bool, exit_code: int | None, stderr: str, facts: FactsOutcome | None) -> str | None:
    """Failure category of one execution, or None when it succeeded.

    An overrun is `timeout`. A non-zero exit takes the SDK category from standard error, else `execution-failed`.
    A zero exit whose facts do not validate is `invalid-result`.
    """
    if timed_out:
        return TIMEOUT
    if exit_code != 0:
        return sdk_failure_category(stderr) or EXECUTION_FAILED
    if facts is not None and not facts.valid:
        return INVALID_RESULT
    return None
