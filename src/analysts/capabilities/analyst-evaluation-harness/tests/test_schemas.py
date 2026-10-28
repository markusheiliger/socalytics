import json
from pathlib import Path
from typing import Any

import pytest
from jsonschema import Draft202012Validator

from support.builders import build_config, build_manifest

PROJECT_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = PROJECT_ROOT.parents[3]
SCHEMAS_DIR = PROJECT_ROOT / "schemas"
SPEC_FOLDER = "20261023-082736-analyst-evaluation-harness"
SPEC_CONTRACT_DIRS = (
    REPOSITORY_ROOT / "specs" / SPEC_FOLDER / "contracts",
    REPOSITORY_ROOT / "specs" / "archive" / SPEC_FOLDER / "contracts",
)

SCHEMA_NAMES = (
    "corpus-manifest.schema.json",
    "evaluation-config.schema.json",
    "evaluation-report.schema.json",
)


def _load(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _spec_contracts() -> Path | None:
    return next((folder for folder in SPEC_CONTRACT_DIRS if folder.is_dir()), None)


@pytest.mark.parametrize("name", SCHEMA_NAMES)
def test_schema_is_valid_json_schema_2020_12(name: str) -> None:
    schema = _load(SCHEMAS_DIR / name)

    assert schema.get("$schema") == "https://json-schema.org/draft/2020-12/schema"
    Draft202012Validator.check_schema(schema)


def test_exactly_the_three_schemas_are_shipped() -> None:
    assert sorted(path.name for path in SCHEMAS_DIR.glob("*.json")) == sorted(SCHEMA_NAMES)


@pytest.mark.parametrize("name", SCHEMA_NAMES)
def test_schema_equals_the_spec_contract_copy(name: str) -> None:
    contracts = _spec_contracts()
    if contracts is None:
        pytest.skip("the spec contracts folder does not exist")

    assert _load(SCHEMAS_DIR / name) == _load(contracts / name)


def test_manifest_builder_document_validates() -> None:
    schema = _load(SCHEMAS_DIR / "corpus-manifest.schema.json")

    errors = list(Draft202012Validator(schema).iter_errors(build_manifest()))

    assert errors == []


def test_config_builder_document_validates() -> None:
    schema = _load(SCHEMAS_DIR / "evaluation-config.schema.json")

    errors = list(Draft202012Validator(schema).iter_errors(build_config()))

    assert errors == []


def test_schemas_reject_an_empty_document() -> None:
    for name in ("corpus-manifest.schema.json", "evaluation-config.schema.json"):
        validator = Draft202012Validator(_load(SCHEMAS_DIR / name))
        assert list(validator.iter_errors({})) != []
