import copy
import json
from decimal import Decimal
from pathlib import Path
from types import SimpleNamespace

import pytest
from jsonschema import Draft202012Validator

from media_context_classification.facts import shot_boundary_fact

SCHEMA_PATH = Path(__file__).resolve().parent.parent / "schemas" / "shot-boundary.payload.schema.json"


@pytest.fixture(scope="module")
def validator() -> Draft202012Validator:
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def _boundary(rule: str, measure: str, context_mean: str | None, previous_frame_ms: int | None):
    return SimpleNamespace(
        frame_index=12,
        start_ms=4_000,
        rule=rule,
        measure=Decimal(measure),
        context_mean=None if context_mean is None else Decimal(context_mean),
        previous_frame_ms=previous_frame_ms,
    )


def _payload(rule: str = "hard-cut", previous_frame_ms: int | None = 3_960) -> dict:
    context_mean = "4.2504" if rule == "adaptive-cut" else None
    measure = "12.0005" if rule == "adaptive-cut" else "97.1234"
    fact = shot_boundary_fact(_boundary(rule, measure, context_mean, previous_frame_ms), 4_040, 10_000)
    return fact.payload


@pytest.mark.parametrize("rule", ["hard-cut", "adaptive-cut"])
@pytest.mark.parametrize("previous_frame_ms", [3_960, None], ids=["with-predecessor", "no-predecessor"])
def test_produced_payloads_are_valid(validator, rule, previous_frame_ms):
    payload = _payload(rule, previous_frame_ms)
    assert list(validator.iter_errors(payload)) == []
    assert ("adaptiveFloor" in payload) == (rule == "adaptive-cut")


def _with(payload: dict, **changes) -> dict:
    result = copy.deepcopy(payload)
    result.update(changes)
    return result


def _without(payload: dict, key: str) -> dict:
    result = copy.deepcopy(payload)
    del result[key]
    return result


def _nested(payload: dict, **changes) -> dict:
    result = copy.deepcopy(payload)
    result["settings"].update(changes)
    return result


@pytest.mark.parametrize(
    "build",
    [
        lambda hard, adaptive: _with(hard, unknown=1),
        lambda hard, adaptive: _nested(hard, unknown=1),
        lambda hard, adaptive: _with(hard, adaptiveFloor=8.0),
        lambda hard, adaptive: _without(hard, "rule"),
        lambda hard, adaptive: _without(hard, "changeMeasure"),
        lambda hard, adaptive: _without(hard, "hardThreshold"),
        lambda hard, adaptive: _without(hard, "frameIndex"),
        lambda hard, adaptive: _without(hard, "previousShotLastFrameMs"),
        lambda hard, adaptive: _without(hard, "settings"),
        lambda hard, adaptive: _without(adaptive, "adaptiveFloor"),
        lambda hard, adaptive: _without(adaptive, "adaptiveRatio"),
        lambda hard, adaptive: _without(adaptive, "contextMean"),
        lambda hard, adaptive: _with(hard, rule="adaptive-cut"),
        lambda hard, adaptive: _with(hard, rule="other"),
        lambda hard, adaptive: _with(hard, changeMeasure=255.001),
        lambda hard, adaptive: _with(hard, changeMeasure=-0.001),
        lambda hard, adaptive: _with(hard, frameIndex=0),
        lambda hard, adaptive: _with(hard, previousShotLastFrameMs="3960"),
        lambda hard, adaptive: _nested(hard, adaptiveWindow=0),
    ],
    ids=[
        "unknown-property",
        "unknown-settings-property",
        "adaptive-field-on-hard-cut",
        "missing-rule",
        "missing-change-measure",
        "missing-hard-threshold",
        "missing-frame-index",
        "missing-previous-shot-last-frame",
        "missing-settings",
        "missing-adaptive-floor",
        "missing-adaptive-ratio",
        "missing-context-mean",
        "adaptive-rule-without-adaptive-fields",
        "unknown-rule",
        "measure-above-range",
        "measure-below-range",
        "frame-index-zero",
        "previous-frame-not-integer",
        "settings-window-zero",
    ],
)
def test_invalid_payloads_are_rejected(validator, build):
    payload = build(_payload("hard-cut"), _payload("adaptive-cut"))
    assert not validator.is_valid(payload)
