import json
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator

SCHEMA_PATH = Path(__file__).resolve().parent.parent / "schemas" / "frame-second-probe.payload.schema.json"


@pytest.fixture(scope="module")
def validator() -> Draft202012Validator:
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def test_zero_frames_without_mean_luma_is_valid(validator):
    assert validator.is_valid({"decodedFrames": 0})


def test_frames_with_mean_luma_is_valid(validator):
    payload = {"decodedFrames": 25, "meanLuma": {"min": 101.236, "mean": 104.512, "max": 108.004}}
    assert validator.is_valid(payload)


@pytest.mark.parametrize(
    "payload",
    [
        {"decodedFrames": -1},
        {"decodedFrames": 0, "unknown": 1},
        {"decodedFrames": 1, "meanLuma": {"min": 1, "mean": 2, "max": 255.001}},
        {"decodedFrames": 1, "meanLuma": {"min": 1, "max": 3}},
    ],
    ids=["negative-count", "unknown-property", "luma-above-255", "missing-mean"],
)
def test_invalid_payloads_are_rejected(validator, payload):
    assert not validator.is_valid(payload)
