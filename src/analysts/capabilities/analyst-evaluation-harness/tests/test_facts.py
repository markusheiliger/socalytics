from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness.facts import (
    EXECUTION_FAILED,
    INVALID_RESULT,
    TIMEOUT,
    FactsOutcome,
    classify_failure,
    read_facts,
    sdk_failure_category,
)
from support.builders import fact


def artifact(*facts: dict[str, Any], **overrides: Any) -> dict[str, Any]:
    document: dict[str, Any] = {
        "contractVersion": "1.0.0",
        "capability": {"id": "scripted", "version": "1.0.0"},
        "schemaVersion": "1.0.0",
        "outputType": "foundational-facts",
        "facts": list(facts),
    }
    document.update(overrides)
    return document


def write_facts(result_dir: Path, content: str | bytes) -> None:
    result_dir.mkdir(parents=True, exist_ok=True)
    path = result_dir / "facts.json"
    if isinstance(content, bytes):
        path.write_bytes(content)
    else:
        path.write_text(content, encoding="utf-8")


def test_valid_facts_are_returned_in_order(tmp_path: Path) -> None:
    write_facts(tmp_path, json.dumps(artifact(fact(2000, 3000, confidence=0.5), fact(0, 500))))

    outcome = read_facts(tmp_path)

    assert outcome.valid
    assert outcome.category is None and outcome.reason is None
    assert outcome.facts is not None
    assert [(item.kind, item.start_ms, item.end_ms, item.confidence) for item in outcome.facts] == [
        ("test-event", 2000, 3000, 0.5),
        ("test-event", 0, 500, 1.0),
    ]


def test_an_artifact_without_facts_is_valid(tmp_path: Path) -> None:
    write_facts(tmp_path, json.dumps(artifact()))

    outcome = read_facts(tmp_path)

    assert outcome.valid
    assert outcome.facts == ()


def test_a_missing_file_is_an_invalid_result(tmp_path: Path) -> None:
    outcome = read_facts(tmp_path)

    assert not outcome.valid
    assert outcome.category == INVALID_RESULT
    assert outcome.reason == "facts.json is missing"


def test_an_unreadable_file_is_an_invalid_result(tmp_path: Path) -> None:
    (tmp_path / "facts.json").mkdir()

    outcome = read_facts(tmp_path)

    assert outcome.category == INVALID_RESULT
    assert outcome.reason == "facts.json is unreadable"


@pytest.mark.parametrize("content", ["{not json", "", b"\xff\xfe\x00"])
def test_content_that_is_not_json_is_an_invalid_result(tmp_path: Path, content: str | bytes) -> None:
    write_facts(tmp_path, content)

    outcome = read_facts(tmp_path)

    assert outcome.category == INVALID_RESULT
    assert outcome.reason == "facts.json is not valid JSON"


@pytest.mark.parametrize(
    "document",
    [
        [],
        artifact(contractVersion="2.0.0"),
        artifact(outputType="unknown"),
        artifact(extra="not allowed"),
        artifact(fact(0, 500, confidence=1.5)),
        artifact(fact(0, 500, kind="Not A Key")),
        artifact({"kind": "test-event", "startMs": -1, "endMs": 10, "confidence": 1, "payload": {}}),
    ],
    ids=["array", "contract-version", "output-type", "extra-field", "confidence", "kind", "negative-start"],
)
def test_a_document_that_breaks_the_fact_artifact_model_is_an_invalid_result(
    tmp_path: Path, document: Any
) -> None:
    write_facts(tmp_path, json.dumps(document))

    outcome = read_facts(tmp_path)

    assert outcome.category == INVALID_RESULT
    assert outcome.reason is not None
    assert outcome.reason.startswith("facts.json does not match the fact artifact")


@pytest.mark.parametrize("start_ms,end_ms", [(1000, 1000), (2000, 1500)])
def test_a_fact_that_does_not_end_after_it_starts_is_an_invalid_result(
    tmp_path: Path, start_ms: int, end_ms: int
) -> None:
    write_facts(tmp_path, json.dumps(artifact(fact(0, 500), fact(start_ms, end_ms))))

    outcome = read_facts(tmp_path)

    assert outcome.category == INVALID_RESULT
    assert outcome.reason == "a fact ends before it starts"


def test_the_reason_never_echoes_content(tmp_path: Path) -> None:
    write_facts(tmp_path, json.dumps(artifact(fact(0, 500, payload={"secret": "hidden-value"}), confidence="hidden")))

    outcome = read_facts(tmp_path)

    assert outcome.reason is not None and "hidden" not in outcome.reason


def test_sdk_failure_category_reads_the_last_line() -> None:
    stderr = (
        "some log\n"
        "socalytics-analyst: failed input-invalid\n"
        "more log\n"
        "  socalytics-analyst: failed media-unreadable  \n"
    )

    assert sdk_failure_category(stderr) == "media-unreadable"


@pytest.mark.parametrize(
    "stderr",
    ["", "Traceback (most recent call last)\nValueError\n", "prefix socalytics-analyst: failed x", "socalytics-analyst: failed"],
)
def test_sdk_failure_category_is_none_without_a_failure_line(stderr: str) -> None:
    assert sdk_failure_category(stderr) is None


VALID = FactsOutcome(facts=())
INVALID = FactsOutcome(facts=None, category=INVALID_RESULT, reason="facts.json is missing")


@pytest.mark.parametrize(
    "kwargs,expected",
    [
        (dict(timed_out=True, exit_code=None, stderr="", facts=None), TIMEOUT),
        (
            dict(timed_out=True, exit_code=1, stderr="socalytics-analyst: failed input-invalid", facts=None),
            TIMEOUT,
        ),
        (
            dict(timed_out=False, exit_code=1, stderr="socalytics-analyst: failed input-invalid\n", facts=None),
            "input-invalid",
        ),
        (dict(timed_out=False, exit_code=1, stderr="Traceback", facts=None), EXECUTION_FAILED),
        (dict(timed_out=False, exit_code=None, stderr="", facts=None), EXECUTION_FAILED),
        (dict(timed_out=False, exit_code=0, stderr="", facts=INVALID), INVALID_RESULT),
        (dict(timed_out=False, exit_code=0, stderr="", facts=VALID), None),
        (dict(timed_out=False, exit_code=0, stderr="", facts=None), None),
    ],
    ids=[
        "timeout",
        "timeout-wins",
        "sdk-category",
        "crash",
        "no-exit-code",
        "invalid-result",
        "success",
        "success-without-read",
    ],
)
def test_classify_failure(kwargs: dict[str, Any], expected: str | None) -> None:
    assert classify_failure(**kwargs) == expected


def test_a_failing_exit_wins_over_unvalidated_facts() -> None:
    assert (
        classify_failure(timed_out=False, exit_code=2, stderr="", facts=INVALID) == EXECUTION_FAILED
    )
