import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from socalytics_analyst.models.facts import FACTS_MEDIA_TYPE, Fact, FactArtifact

FACT = {
    "kind": "player-detected",
    "startMs": 120000,
    "endMs": 120040,
    "confidence": 0.93,
    "payload": {"trackId": "t-7", "box": [0.41, 0.22, 0.05, 0.12]},
}
VALID = {
    "contractVersion": "1.0.0",
    "capability": {"id": "player-detection", "version": "1.0.0"},
    "schemaVersion": "1.0.0",
    "outputType": "foundational-facts",
    "facts": [FACT],
}
SCHEMA_DIR = Path(__file__).parents[1] / "src/socalytics_analyst/contracts/analysis/fact-artifact/v1"


def test_media_type():
    assert FACTS_MEDIA_TYPE == "application/vnd.socalytics.facts+json"


def test_valid_artifact_round_trips():
    artifact = FactArtifact.model_validate(VALID)
    assert artifact.facts[0].start_ms == 120000
    assert artifact.model_dump(by_alias=True) == VALID


def test_empty_facts_and_soccer_concepts():
    FactArtifact.model_validate({**VALID, "facts": [], "outputType": "soccer-concepts"})


@pytest.mark.parametrize("missing", list(VALID))
def test_artifact_required_fields(missing):
    with pytest.raises(ValidationError):
        FactArtifact.model_validate({k: v for k, v in VALID.items() if k != missing})


@pytest.mark.parametrize("missing", list(FACT))
def test_fact_required_fields(missing):
    with pytest.raises(ValidationError):
        Fact.model_validate({k: v for k, v in FACT.items() if k != missing})


@pytest.mark.parametrize(
    "bad",
    [
        {"startMs": -1},
        {"endMs": 0},
        {"confidence": 1.01},
        {"confidence": -0.1},
        {"payload": []},
        {"kind": "Player"},
        {"extra": 1},
    ],
)
def test_fact_rejects_invalid(bad):
    with pytest.raises(ValidationError):
        Fact.model_validate({**FACT, **bad})


@pytest.mark.parametrize("bad", [{"outputType": "other"}, {"extra": 1}, {"contractVersion": "2.0.0"}])
def test_artifact_rejects_invalid(bad):
    with pytest.raises(ValidationError):
        FactArtifact.model_validate({**VALID, **bad})


def test_properties_match_schema():
    schema = json.loads((SCHEMA_DIR / "fact-artifact.schema.json").read_text())
    fields = {f.alias for f in FactArtifact.model_fields.values()}
    assert fields == set(schema["properties"]) == set(schema["required"])
    fact_fields = {f.alias for f in Fact.model_fields.values()}
    fact_schema = schema["$defs"]["fact"]
    assert fact_fields == set(fact_schema["properties"]) == set(fact_schema["required"])
