import json
from pathlib import Path

import pytest

from socalytics_analyst import errors, schemas

CONTRACTS = Path(__file__).resolve().parents[4] / "contracts" / "analysis"

KIND_FOLDERS = {
    "analyst-job": "analyst-job",
    "analyst-attempt": "analyst-attempt",
    "result-manifest": "result-manifest",
    "fact-artifact": "fact-artifact",
}


def _examples(kind: str, group: str) -> list[Path]:
    folder = CONTRACTS / KIND_FOLDERS[kind] / "v1" / "examples" / group
    return sorted(path for path in folder.glob("*.json") if path.name != "expectations.json")


def _load(path: Path) -> object:
    return json.loads(path.read_text(encoding="utf-8"))


@pytest.mark.parametrize("kind", sorted(KIND_FOLDERS))
def test_valid_examples_validate(kind: str) -> None:
    examples = _examples(kind, "valid")
    assert examples
    for path in examples:
        schemas.validate(kind, _load(path))


@pytest.mark.parametrize("kind", sorted(KIND_FOLDERS))
def test_invalid_examples_are_rejected_with_input_invalid(kind: str) -> None:
    examples = _examples(kind, "invalid")
    assert examples
    for path in examples:
        with pytest.raises(errors.InputError) as raised:
            schemas.validate(kind, _load(path))
        assert raised.value.category in (errors.INPUT_INVALID, errors.UNSUPPORTED_CONTRACT_VERSION)


def test_error_names_keyword_and_path() -> None:
    document = _load(CONTRACTS / "analyst-attempt" / "v1" / "examples" / "invalid" / "zero-attempt-number.json")
    with pytest.raises(errors.InputError) as raised:
        schemas.validate("analyst-attempt", document)
    assert raised.value.category == errors.INPUT_INVALID
    assert "minimum at /attemptNumber" in str(raised.value)


def test_error_does_not_leak_document_values() -> None:
    document = {"contractVersion": "1.0.0", "attemptId": "secret-value", "attemptNumber": 1, "extra": "leaky"}
    with pytest.raises(errors.InputError) as raised:
        schemas.validate("analyst-attempt", document)
    assert "additionalProperties" in str(raised.value)
    assert "secret-value" not in str(raised.value)
    assert "leaky" not in str(raised.value)


@pytest.mark.parametrize("version", ["2.0.0", "0.9.0", "1.1.0", "1.9.3"])
def test_unsupported_attempt_versions_are_refused(version: str) -> None:
    document = {"contractVersion": version, "attemptId": "a", "attemptNumber": 1}
    with pytest.raises(errors.InputError) as raised:
        schemas.validate("analyst-attempt", document)
    assert raised.value.category == errors.UNSUPPORTED_CONTRACT_VERSION


def test_newer_job_minor_is_refused_and_supported_minors_select_their_release() -> None:
    assert schemas.select_version("analyst-job", {"contractVersion": "1.0.0"}) == "1.0.0"
    assert schemas.select_version("analyst-job", {"contractVersion": "1.1.0"}) == "1.1.0"
    with pytest.raises(errors.InputError) as raised:
        schemas.select_version("analyst-job", {"contractVersion": "1.2.0"})
    assert raised.value.category == errors.UNSUPPORTED_CONTRACT_VERSION


def test_supported_versions_follow_research_r5() -> None:
    assert schemas.supported_versions("analyst-job") == ("1.0.0", "1.1.0")
    assert schemas.supported_versions("analyst-attempt") == ("1.0.0",)
    assert schemas.supported_versions("upstream-results") == ("1.0.0", "1.1.0")
    assert schemas.supported_versions("result-manifest") == ("1.0.0", "1.1.0")
    assert schemas.supported_versions("fact-artifact") == ("1.0.0",)


def test_format_checking_rejects_bad_timestamp() -> None:
    document = _load(CONTRACTS / "analyst-job" / "v1" / "examples" / "valid" / "segment-job.json")
    document["createdAt"] = "not-a-timestamp"
    with pytest.raises(errors.InputError) as raised:
        schemas.validate("analyst-job", document)
    assert "/createdAt" in str(raised.value)


def test_output_validation_raises_result_error() -> None:
    with pytest.raises(errors.ResultError) as raised:
        schemas.validate("analyst-attempt", {"contractVersion": "1.0.0"}, output=True)
    assert raised.value.category == errors.OUTPUT_INVALID
    assert raised.value.exit_code == 1


def test_error_exit_codes() -> None:
    assert errors.InputError.exit_code == 2
    assert errors.ResultError.exit_code == 1
    assert issubclass(errors.InputError, errors.AnalystSdkError)
    assert issubclass(errors.ResultError, errors.AnalystSdkError)
