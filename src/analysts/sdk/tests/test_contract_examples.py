import json
import re
from pathlib import Path

import pytest
from pydantic import ValidationError

from socalytics_analyst import errors, schemas
from socalytics_analyst.models import (
    AttemptContext,
    FactArtifact,
    LogicalJob,
    ResultManifest,
    SegmentManifest,
    StagedCoverage,
)

CONTRACTS = Path(__file__).resolve().parents[4] / "contracts" / "analysis"

MODELS = {
    "analyst-job": LogicalJob,
    "analyst-attempt": AttemptContext,
    "result-manifest": ResultManifest,
    "fact-artifact": FactArtifact,
}

_KEYWORD = re.compile(r"(?:: |; )(\w+) at /")


def _examples(kind: str, group: str) -> list[Path]:
    folder = CONTRACTS / kind / "v1" / "examples" / group
    return sorted(path for path in folder.glob("*.json") if path.name != "expectations.json")


def _expectations(kind: str) -> dict[str, str]:
    path = CONTRACTS / kind / "v1" / "examples" / "invalid" / "expectations.json"
    return json.loads(path.read_text(encoding="utf-8"))


def _load(path: Path) -> object:
    return json.loads(path.read_text(encoding="utf-8"))


def _keywords(error: errors.AnalystSdkError) -> set[str]:
    return set(_KEYWORD.findall(str(error)))


def _id(path: Path) -> str:
    return f"{path.parent.parent.parent.parent.name}/{path.parent.name}/{path.name}"


VALID = [(kind, path) for kind in MODELS for path in _examples(kind, "valid")]
INVALID = [(kind, path) for kind in MODELS for path in _examples(kind, "invalid")]
UPSTREAM_VALID = _examples("upstream-results", "valid")
UPSTREAM_INVALID = _examples("upstream-results", "invalid")


@pytest.mark.parametrize("kind", sorted(MODELS))
def test_every_kind_has_valid_and_invalid_examples(kind: str) -> None:
    assert _examples(kind, "valid")
    assert _examples(kind, "invalid")


@pytest.mark.parametrize(("kind", "path"), VALID, ids=[_id(p) for _, p in VALID])
def test_valid_example_is_accepted_by_schema_and_model(kind: str, path: Path) -> None:
    document = _load(path)
    schemas.validate(kind, document)
    MODELS[kind].model_validate(document)


@pytest.mark.parametrize(("kind", "path"), INVALID, ids=[_id(p) for _, p in INVALID])
def test_invalid_example_is_rejected_by_schema_and_model(kind: str, path: Path) -> None:
    document = _load(path)
    expected = _expectations(kind)[path.name]
    with pytest.raises(errors.InputError) as raised:
        schemas.validate(kind, document)
    assert expected in _keywords(raised.value), str(raised.value)
    with pytest.raises(ValidationError):
        MODELS[kind].model_validate(document)


@pytest.mark.parametrize("kind", sorted(MODELS))
def test_every_invalid_example_has_an_expectation(kind: str) -> None:
    names = {path.name for path in _examples(kind, "invalid")}
    assert names == set(_expectations(kind))


@pytest.mark.parametrize("path", UPSTREAM_VALID, ids=[p.name for p in UPSTREAM_VALID])
def test_valid_upstream_page_validates_and_its_parts_parse(path: Path) -> None:
    page = _load(path)
    schemas.validate("upstream-results", page)

    StagedCoverage.model_validate({"upstream": page["upstream"], "coverage": page["coverage"]})
    for result in page["results"]:
        ResultManifest.model_validate(result["manifest"])


@pytest.mark.parametrize("path", UPSTREAM_INVALID, ids=[p.name for p in UPSTREAM_INVALID])
def test_invalid_upstream_page_is_rejected_with_its_keyword(path: Path) -> None:
    expected = _expectations("upstream-results")[path.name]
    with pytest.raises(errors.InputError) as raised:
        schemas.validate("upstream-results", _load(path))
    assert expected in _keywords(raised.value), str(raised.value)


def test_every_invalid_upstream_example_has_an_expectation() -> None:
    assert {path.name for path in UPSTREAM_INVALID} == set(_expectations("upstream-results"))


SEGMENT_MANIFEST_EXAMPLES = CONTRACTS.parent / "segments" / "segment-manifest" / "v1" / "examples"


def _segment_examples(group: str) -> list[Path]:
    folder = SEGMENT_MANIFEST_EXAMPLES / group
    return sorted(path for path in folder.glob("*.json") if path.name != "expectations.json")


@pytest.mark.parametrize("path", _segment_examples("valid"), ids=lambda p: p.name)
def test_valid_segment_manifest_is_accepted_by_schema_and_model(path: Path) -> None:
    document = _load(path)
    schemas.validate("segment-manifest", document)
    SegmentManifest.model_validate(document)


@pytest.mark.parametrize("path", _segment_examples("invalid"), ids=lambda p: p.name)
def test_invalid_segment_manifest_is_rejected_by_schema_and_model(path: Path) -> None:
    expected = json.loads((SEGMENT_MANIFEST_EXAMPLES / "invalid" / "expectations.json").read_text(encoding="utf-8"))[
        path.name
    ]
    document = _load(path)
    with pytest.raises(errors.InputError) as raised:
        schemas.validate("segment-manifest", document)
    assert expected in _keywords(raised.value), str(raised.value)
    with pytest.raises(ValidationError):
        SegmentManifest.model_validate(document)


def test_every_invalid_segment_manifest_has_an_expectation() -> None:
    expectations = json.loads((SEGMENT_MANIFEST_EXAMPLES / "invalid" / "expectations.json").read_text(encoding="utf-8"))
    assert {path.name for path in _segment_examples("invalid")} == set(expectations)
