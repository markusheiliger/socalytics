import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from socalytics_analyst.models.manifest import AnalystManifest, ResultManifest

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


ANALYST_EXAMPLES = (
    Path(__file__).resolve().parents[4] / "contracts" / "analysis" / "analyst-manifest" / "v1" / "examples"
)
PAYLOAD_VALID = ANALYST_EXAMPLES / "valid" / "segment-with-payload-schemas.json"
CODE_CHECKED = {
    name: expected["code"]
    for name, expected in load(ANALYST_EXAMPLES / "invalid" / "expectations.json").items()
    if isinstance(expected, dict) and "code" in expected
}


def test_manifest_without_payload_schemas_behaves_as_before():
    document = load(ANALYST_EXAMPLES / "valid" / "segment-low-level.json")
    manifest = AnalystManifest.model_validate(document)
    assert manifest.output.fact_payload_schemas is None


def test_manifest_with_payload_schemas_parses_and_round_trips():
    document = load(PAYLOAD_VALID)
    manifest = AnalystManifest.model_validate(document)
    assert document["manifestVersion"] == "1.2.0"
    assert set(manifest.output.fact_payload_schemas) == {"player-detection", "frame-quality"}
    assert manifest.model_dump(by_alias=True, exclude_none=True) == document


@pytest.mark.parametrize("name,code", sorted(CODE_CHECKED.items()))
def test_code_checked_invalid_examples_give_the_named_refusal(name, code):
    document = load(ANALYST_EXAMPLES / "invalid" / name)
    with pytest.raises(ValidationError) as raised:
        AnalystManifest.model_validate(document)
    assert code in str(raised.value)


def test_payload_schemas_need_manifest_version_1_2_0():
    document = load(PAYLOAD_VALID)
    with pytest.raises(ValidationError):
        AnalystManifest.model_validate({**document, "manifestVersion": "1.1.0"})


def test_unknown_keyword_and_external_ref_are_refused():
    document = load(PAYLOAD_VALID)
    schemas = document["output"]["factPayloadSchemas"]
    unknown = {**schemas, "frame-quality": {**schemas["frame-quality"], "pattern": "^a$"}}
    with pytest.raises(ValidationError):
        AnalystManifest.model_validate({**document, "output": {**document["output"], "factPayloadSchemas": unknown}})
    external = {**schemas, "frame-quality": {"$ref": "https://example.com/schema.json"}}
    with pytest.raises(ValidationError):
        AnalystManifest.model_validate({**document, "output": {**document["output"], "factPayloadSchemas": external}})
