import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from socalytics_analyst.models.manifest import ResultManifest

EXAMPLES = Path(__file__).resolve().parents[4] / "contracts" / "analysis" / "result-manifest" / "v1" / "examples"
VALID = sorted((EXAMPLES / "valid").glob("*.json"))
INVALID = sorted(p for p in (EXAMPLES / "invalid").glob("*.json") if p.name != "expectations.json")


def load(path):
    return json.loads(path.read_text(encoding="utf-8"))


@pytest.mark.parametrize("path", VALID, ids=lambda p: p.name)
def test_valid_examples_parse_and_round_trip(path):
    document = load(path)
    manifest = ResultManifest.model_validate(document)
    assert manifest.model_dump(by_alias=True, exclude_none=True) == document


@pytest.mark.parametrize("path", INVALID, ids=lambda p: p.name)
def test_invalid_examples_are_rejected(path):
    with pytest.raises(ValidationError):
        ResultManifest.model_validate(load(path))


def test_scope_requires_exactly_one_of_segment_or_match():
    document = load(next(p for p in VALID if p.name == "segment-result.json"))
    both = {**document, "scope": {**document["scope"], "match": True}}
    with pytest.raises(ValidationError):
        ResultManifest.model_validate(both)
    neither = {**document, "scope": {k: v for k, v in document["scope"].items() if k != "segment"}}
    with pytest.raises(ValidationError):
        ResultManifest.model_validate(neither)


def test_artifact_and_confidence_limits():
    document = load(next(p for p in VALID if p.name == "segment-result.json"))
    artifact = {**document["artifacts"][0], "sizeBytes": 0}
    with pytest.raises(ValidationError):
        ResultManifest.model_validate({**document, "artifacts": [artifact]})
    with pytest.raises(ValidationError):
        ResultManifest.model_validate({**document, "artifacts": [document["artifacts"][0]] * 257})
    with pytest.raises(ValidationError):
        ResultManifest.model_validate({**document, "confidence": 1.01})
    with pytest.raises(ValidationError):
        ResultManifest.model_validate({**document, "outputType": "other"})


def test_input_coverage_limits_and_unit_subject():
    document = load(next(p for p in VALID if p.name == "match-result-partial-coverage.json"))
    coverage = document["inputCoverage"][0]
    unit = {"segmentId": "s1", "state": "accepted"}
    too_many = {**document, "inputCoverage": [{**coverage, "units": [unit] * 1025}]}
    with pytest.raises(ValidationError):
        ResultManifest.model_validate(too_many)
    both = {**document, "inputCoverage": [{**coverage, "units": [{**unit, "match": True}]}]}
    with pytest.raises(ValidationError):
        ResultManifest.model_validate(both)
    assert ResultManifest.model_validate({**document, "inputCoverage": None}).input_coverage is None
