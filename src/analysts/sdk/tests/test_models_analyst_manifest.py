import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from socalytics_analyst.models.manifest import AnalystManifest, check_fact_payload_schemas

ROOT = Path(__file__).resolve().parents[4] / "contracts" / "analysis" / "analyst-manifest" / "v1" / "examples"
VALID = sorted((ROOT / "valid").glob("*.json"))
INVALID = sorted(p for p in (ROOT / "invalid").glob("*.json") if p.name != "expectations.json")
EXPECTATIONS = json.loads((ROOT / "invalid" / "expectations.json").read_text(encoding="utf-8"))


def load(path):
    return json.loads(path.read_text(encoding="utf-8"))


@pytest.mark.parametrize("path", VALID, ids=lambda p: p.name)
def test_valid_examples_parse(path):
    manifest = AnalystManifest.model_validate(load(path))
    declared = load(path)["output"].get("factPayloadSchemas")
    assert (manifest.output.fact_payload_schemas is not None) == (declared is not None)


@pytest.mark.parametrize("path", INVALID, ids=lambda p: p.name)
def test_invalid_examples_are_rejected(path):
    expected = EXPECTATIONS[path.name]
    with pytest.raises(ValidationError) as caught:
        AnalystManifest.model_validate(load(path))
    if isinstance(expected, dict) and "code" in expected:
        assert expected["code"] in str(caught.value)


@pytest.mark.parametrize(
    "path", [p for p in INVALID if isinstance(EXPECTATIONS[p.name], dict) and "code" in EXPECTATIONS[p.name]],
    ids=lambda p: p.name,
)
def test_code_checked_refusals_give_the_named_code(path):
    schemas = load(path)["output"]["factPayloadSchemas"]
    refusal = check_fact_payload_schemas(schemas)
    assert refusal is not None and refusal[0] == EXPECTATIONS[path.name]["code"]


def test_unresolved_ref_and_non_schema_codes():
    root = {"type": "object", "properties": {"a": {"$ref": "#/$defs/missing"}}}
    assert check_fact_payload_schemas({"k": root}) == ("payload-schema-unresolved-ref", "k")
    assert check_fact_payload_schemas({"k": {"type": "object", "properties": {"a": 1}}}) == (
        "payload-schema-not-a-schema",
        "k",
    )
    assert check_fact_payload_schemas({"k": {"type": "object"}}) is None
