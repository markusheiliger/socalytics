import json
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator
from referencing import Registry, Resource

import reference_media_probe

PROJECT_ROOT = Path(__file__).resolve().parent.parent
MANIFEST_PATH = PROJECT_ROOT / "analyst-manifest.json"


def _repository_root() -> Path:
    for candidate in PROJECT_ROOT.parents:
        if (candidate / "contracts" / "analysis" / "analyst-manifest").is_dir():
            return candidate
    raise AssertionError("repository root with contracts/ not found")


@pytest.fixture(scope="module")
def manifest() -> dict:
    return json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def validator() -> Draft202012Validator:
    contracts = _repository_root() / "contracts"
    schema = json.loads(
        (contracts / "analysis" / "analyst-manifest" / "v1" / "analyst-manifest.schema.json").read_text(encoding="utf-8")
    )
    common = json.loads((contracts / "common" / "v1" / "common.schema.json").read_text(encoding="utf-8"))
    registry = Registry().with_resource(common["$id"], Resource.from_contents(common))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, registry=registry)


def test_manifest_is_valid_against_contract(validator, manifest):
    errors = sorted(validator.iter_errors(manifest), key=lambda error: list(error.path))
    assert errors == []


def test_manifest_capability_matches_package_constants(manifest):
    assert manifest["capability"]["id"] == reference_media_probe.CAPABILITY_ID
    assert manifest["capability"]["version"] == reference_media_probe.CAPABILITY_VERSION


def test_manifest_declaration_values(manifest):
    assert manifest["capability"]["tier"] == "low-level"
    assert manifest["capability"]["executionScope"] == "segment"
    assert manifest["upstream"] == []
    assert manifest["videoAccess"] == "required"
    assert manifest["output"] == {"schemaVersion": "1.0.0"}
    assert manifest["compatibleProfiles"] == ["reference-media-probe-dev"]
    assert manifest["model"]["digest"] == "sha-256:" + "0" * 64
    assert manifest["runtime"] == {"constraints": ["linux-amd64"], "accelerators": []}
    assert manifest["resources"] == {"memory": "512Mi", "cpu": "1"}
    assert manifest["requirements"] == {
        "minMaxAttempts": 1,
        "minLeaseSeconds": 30,
        "minStaleTimeoutSeconds": 60,
        "minExecutionTimeoutSeconds": 300,
    }
    assert manifest["determinism"] == {"deterministic": True, "idempotent": True}
