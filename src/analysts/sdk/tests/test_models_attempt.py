import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from socalytics_analyst.models.attempt import AttemptContext

VALID = {"contractVersion": "1.0.0", "attemptId": "a1", "attemptNumber": 2}


def test_valid_attempt_context():
    attempt = AttemptContext.model_validate(VALID)
    assert attempt.attempt_id == "a1"
    assert attempt.attempt_number == 2
    assert attempt.model_dump(by_alias=True) == VALID


@pytest.mark.parametrize("missing", list(VALID))
def test_required_fields(missing):
    data = {k: v for k, v in VALID.items() if k != missing}
    with pytest.raises(ValidationError):
        AttemptContext.model_validate(data)


@pytest.mark.parametrize(
    "bad",
    [
        {"contractVersion": "2.0.0"},
        {"attemptId": "-a"},
        {"attemptNumber": 0},
        {"fencingToken": "x"},
        {"lease": {}},
    ],
)
def test_rejects_invalid_and_extra_fields(bad):
    with pytest.raises(ValidationError):
        AttemptContext.model_validate({**VALID, **bad})


def test_properties_match_schema():
    schema_path = (
        Path(__file__).parents[1]
        / "src/socalytics_analyst/contracts/analysis/analyst-attempt/v1/analyst-attempt.schema.json"
    )
    schema = json.loads(schema_path.read_text())
    fields = {f.alias or n for n, f in AttemptContext.model_fields.items()}
    assert fields == set(schema["properties"]) == set(schema["required"])
