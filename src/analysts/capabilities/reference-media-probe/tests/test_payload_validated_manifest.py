from __future__ import annotations

import hashlib
import json
import re
from collections.abc import Callable
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from socalytics_analyst import run

from reference_media_probe.__main__ import analyse
from support.mounts import Mount

PROJECT_ROOT = Path(__file__).resolve().parent.parent
VALIDATED_MANIFEST = PROJECT_ROOT / "analyst-manifest.payload-validated.json"
VALIDATED_DOCKERFILE = PROJECT_ROOT / "Dockerfile.payload-validated"
SCHEMA_FILE = PROJECT_ROOT / "schemas" / "frame-second-probe.payload.schema.json"
BASE_MANIFEST = PROJECT_ROOT / "analyst-manifest.json"
BASE_DOCKERFILE = PROJECT_ROOT / "Dockerfile"
BASE_MANIFEST_SHA256 = "31c94a2a7ff4f97611f63bde4c91481677abbbb0433954470c0020b951b56c5f"
WINDOW = {"startMs": 0, "endMs": 5000}
SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]


def _repository_root() -> Path:
    for candidate in PROJECT_ROOT.parents:
        if (candidate / "contracts" / "analysis" / "analyst-manifest").is_dir():
            return candidate
    raise AssertionError("repository root with contracts/ not found")


def _load(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def _dockerfile_value(text: str, pattern: str) -> str:
    match = re.search(pattern, text)
    assert match, f"pattern not found: {pattern}"
    return match.group(1)


@pytest.fixture(scope="module")
def manifest() -> dict:
    return _load(VALIDATED_MANIFEST)


@pytest.fixture(scope="module")
def manifest_validator() -> Draft202012Validator:
    contracts = _repository_root() / "contracts"
    schema = _load(contracts / "analysis" / "analyst-manifest" / "v1" / "releases" / "1.2.0.schema.json")
    common = _load(contracts / "common" / "v1" / "common.schema.json")
    registry = Registry().with_resource(common["$id"], Resource.from_contents(common))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema, registry=registry)


def _run(mount: Mount) -> int:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0


def _mount(media_factory: Callable[..., Path], mount_factory: Callable[..., Mount]) -> Mount:
    return mount_factory(
        media_factory(),
        nominal_window=WINDOW,
        requested_source_interval=WINDOW,
        encoded_interval=WINDOW,
        timeline_spans=SPANS,
    )


def test_manifest_is_valid_against_manifest_1_2_0(manifest_validator, manifest):
    assert manifest["manifestVersion"] == "1.2.0"
    errors = sorted(manifest_validator.iter_errors(manifest), key=lambda error: list(error.path))
    assert errors == []


def test_manifest_declares_capability_1_1_0_with_unchanged_behaviour(manifest):
    base = _load(BASE_MANIFEST)
    assert manifest["capability"] == {**base["capability"], "version": "1.1.0"}
    for name in ("upstream", "videoAccess", "compatibleProfiles", "model", "runtime", "resources", "requirements", "determinism"):
        assert manifest[name] == base[name], name
    assert manifest["output"]["schemaVersion"] == base["output"]["schemaVersion"]


def test_declared_schema_equals_the_schema_file(manifest):
    schemas = manifest["output"]["factPayloadSchemas"]
    assert list(schemas) == ["frame-second-probe"]
    declared = schemas["frame-second-probe"]
    file_schema = _load(SCHEMA_FILE)
    assert {key: value for key, value in file_schema.items() if key != "$schema"} == declared


def test_produced_facts_conform_and_stay_deterministic(media_factory, mount_factory, manifest):
    validator = Draft202012Validator(manifest["output"]["factPayloadSchemas"]["frame-second-probe"])
    media = media_factory()
    results = []
    for _ in range(2):
        mount = mount_factory(
            media,
            nominal_window=WINDOW,
            requested_source_interval=WINDOW,
            encoded_interval=WINDOW,
            timeline_spans=SPANS,
        )
        assert _run(mount) == 0
        results.append((mount.result / "facts.json").read_bytes())

    assert results[0] == results[1]
    facts = json.loads(results[0])["facts"]
    assert len(facts) == 5
    for fact in facts:
        assert fact["kind"] == "frame-second-probe"
        validator.validate(fact["payload"])


def test_dockerfile_variant_is_consistent_with_the_manifest(manifest):
    text = VALIDATED_DOCKERFILE.read_text(encoding="utf-8")
    digest = hashlib.sha256(VALIDATED_MANIFEST.read_bytes()).hexdigest()

    assert _dockerfile_value(text, r'analyst-manifest\.version="([^"]+)"') == manifest["manifestVersion"]
    assert _dockerfile_value(text, r'analyst-manifest\.sha256="([0-9a-f]{64})"') == digest
    assert _dockerfile_value(text, r"ARG MANIFEST_SHA256=([0-9a-f]{64})") == digest
    assert _dockerfile_value(text, r'capability\.id="([^"]+)"') == manifest["capability"]["id"]
    assert _dockerfile_value(text, r'capability\.version="([^"]+)"') == manifest["capability"]["version"]
    assert _dockerfile_value(text, r'model\.digest="([^"]+)"') == manifest["model"]["digest"]
    copies = re.findall(r"^COPY (\S+analyst-manifest\S*\.json) (\S+)$", text, re.M)
    target = "src/analysts/capabilities/reference-media-probe/analyst-manifest.payload-validated.json"
    assert (target, "/opt/socalytics/analyst-manifest.json") in copies
    assert (target, "/tmp/analyst-manifest.json") in copies
    assert text.rstrip().splitlines()[-1] == f"COPY {target} /opt/socalytics/analyst-manifest.json"


def test_dockerfile_variant_keeps_the_same_pins_as_the_base_dockerfile():
    def pins(path: Path) -> list[str]:
        return re.findall(r"^(?:ARG (?!MANIFEST_SHA256)\S+=.*|FROM .*)$", path.read_text(encoding="utf-8"), re.M)

    assert pins(VALIDATED_DOCKERFILE) == pins(BASE_DOCKERFILE)


def test_the_1_0_0_files_are_unchanged():
    base = _load(BASE_MANIFEST)
    assert hashlib.sha256(BASE_MANIFEST.read_bytes()).hexdigest() == BASE_MANIFEST_SHA256
    assert base["manifestVersion"] == "1.0.0"
    assert base["capability"]["version"] == "1.0.0"
    assert base["output"] == {"schemaVersion": "1.0.0"}

    text = BASE_DOCKERFILE.read_text(encoding="utf-8")
    assert BASE_MANIFEST_SHA256 in text
    assert "payload-validated" not in text
    assert _dockerfile_value(text, r'capability\.version="([^"]+)"') == "1.0.0"
