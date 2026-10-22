"""Model fields must match the object definitions of the implemented schemas (FR-002, FR-022)."""

from __future__ import annotations

from collections.abc import Iterator
from typing import Any

import pytest
from pydantic import BaseModel

from socalytics_analyst import schemas
from socalytics_analyst.models import attempt, common, facts, job, manifest, segment, upstream

COMMON_ID = schemas.COMMON_ID
CAPABILITY = common.CapabilityRef

# Document kind -> schema object path (through properties and items, $ref followed) -> model.
MODELS: dict[str, dict[str, type[BaseModel]]] = {
    "analyst-job": {
        "#": job.LogicalJob,
        "#/properties/capability": CAPABILITY,
        "#/properties/segment": common.AnalysisSegmentReference,
        "#/properties/segmentWindow": job.SegmentWindow,
        "#/properties/segmentMedia": job.SegmentMedia,
        "#/properties/workflow": job.WorkflowRef,
        "#/properties/inputs/items": job.InputEntry,
        "#/properties/inputs/items/properties/capability": CAPABILITY,
        "#/properties/inputs/items/properties/acceptedResults/items": common.AcceptedResultLineage,
        "#/properties/inputs/items/properties/acceptedResults/items/properties/capability": CAPABILITY,
        "#/properties/pinned": job.Pinned,
        "#/properties/pinned/properties/policies/items": job.PolicyPin,
        "#/properties/resources": job.Resources,
        "#/properties/attemptBudget": job.AttemptBudget,
        "#/properties/timeouts": job.Timeouts,
        "#/properties/correlation": common.Correlation,
    },
    "analyst-attempt": {"#": attempt.AttemptContext},
    "result-manifest": {
        "#": manifest.ResultManifest,
        "#/properties/scope": manifest.ManifestScope,
        "#/properties/scope/properties/segment": manifest.ManifestSegment,
        "#/properties/provenance": manifest.Provenance,
        "#/properties/provenance/properties/capability": CAPABILITY,
        "#/properties/provenance/properties/analystProfile": manifest.ProfileRef,
        "#/properties/provenance/properties/policyVersions/items": manifest.PolicyVersion,
        "#/properties/provenance/properties/workflow": manifest.WorkflowRef,
        "#/properties/attempt": manifest.ManifestAttempt,
        "#/properties/artifacts/items": manifest.ManifestArtifact,
        "#/properties/validityWindow": manifest.ValidityWindow,
        "#/properties/upstreamLineage/items": manifest.UpstreamLineageEntry,
        "#/properties/upstreamLineage/items/properties/capability": CAPABILITY,
        "#/properties/inputCoverage/items": manifest.InputCoverage,
        "#/properties/inputCoverage/items/properties/capability": CAPABILITY,
        "#/properties/inputCoverage/items/properties/units/items": manifest.InputCoverageUnit,
    },
    "fact-artifact": {
        "#": facts.FactArtifact,
        "#/properties/capability": CAPABILITY,
        "#/properties/facts/items": facts.Fact,
    },
    "segment-manifest": {
        "#": segment.SegmentManifest,
        "#/properties/identity": segment.SegmentIdentity,
        "#/properties/segmentationPolicy": segment.SegmentationPolicy,
        "#/properties/nominalWindow": segment.HalfOpenInterval,
        "#/properties/requestedSourceInterval": segment.HalfOpenInterval,
        "#/properties/encodedInterval": segment.HalfOpenInterval,
        "#/properties/timelineSpans/items": segment.TimelineSpan,
        "#/properties/media": segment.MediaObject,
        "#/properties/producer": segment.Producer,
    },
}

# The SDK reads only these parts of an upstream-results page (as the staged coverage.json); the model of the
# page root covers the `upstream` and `coverage` properties only.
UPSTREAM_PARTIAL_ROOT = "#"
UPSTREAM_MODELS: dict[str, type[BaseModel]] = {
    "#": upstream.StagedCoverage,
    "#/properties/upstream": upstream.StagedUpstream,
    "#/properties/upstream/properties/capability": CAPABILITY,
    "#/properties/upstream/properties/coverage": upstream.CoveragePolicy,
    "#/properties/coverage/items": common.CoverageEntry,
}
UPSTREAM_PATHS = {
    "#",
    "#/properties/upstream",
    "#/properties/upstream/properties/capability",
    "#/properties/upstream/properties/coverage",
    "#/properties/coverage/items",
}


def _current_schema(kind: str) -> tuple[str, dict[str, Any]]:
    folder, stem, _ = schemas.KINDS[kind]
    contents = schemas._read(f"{folder}/v1/{stem}.schema.json")
    return contents["$id"], contents


def _objects(kind: str) -> Iterator[tuple[str, dict[str, Any]]]:
    """Yield every object definition reachable through properties and items, following `$ref`."""
    schema_id, contents = _current_schema(kind)
    registry = schemas.registry()

    def walk(node: Any, resolver: Any, path: str) -> Iterator[tuple[str, dict[str, Any]]]:
        if not isinstance(node, dict):
            return
        if "$ref" in node:
            resolved = resolver.lookup(node["$ref"])
            yield from walk(resolved.contents, resolved.resolver, path)
            return
        if "properties" in node:
            yield path, node
            for name, child in node["properties"].items():
                yield from walk(child, resolver, f"{path}/properties/{name}")
        if "items" in node:
            yield from walk(node["items"], resolver, f"{path}/items")

    yield from walk(contents, registry.resolver(schema_id), "#")


def _compare(
    schema_path: str, node: dict[str, Any], model: type[BaseModel], *, partial: bool = False
) -> list[str]:
    where = f"{schema_path} -> {model.__name__}"
    properties: dict[str, Any] = node["properties"]
    required = set(node.get("required", []))
    problems: list[str] = []

    aliases: dict[str, bool] = {}
    for name, field in model.model_fields.items():
        if field.alias is None:
            problems.append(f"{where}: field {name} has no alias")
            continue
        aliases[field.alias] = field.is_required()

    for alias in sorted(set(aliases) - set(properties)):
        problems.append(f"{where}: model property {alias} is not in the schema")
    if not partial:
        for name in sorted(set(properties) - set(aliases)):
            problems.append(f"{where}: schema property {name} is not in the model")
    for alias in sorted(set(aliases) & set(properties)):
        if aliases[alias] != (alias in required):
            expected = "required" if alias in required else "optional"
            actual = "required" if aliases[alias] else "optional"
            problems.append(f"{where}: property {alias} is {expected} in the schema but {actual} in the model")
    if node.get("additionalProperties") is not False:
        problems.append(f"{where}: schema object is not closed")
    if model.model_config.get("extra") != "forbid":
        problems.append(f"{where}: model is not closed")
    return problems


@pytest.mark.parametrize("kind", sorted(MODELS))
def test_model_fields_match_schema_objects(kind: str) -> None:
    mapped = MODELS[kind]
    found = dict(_objects(kind))

    problems = [f"{path}: schema object has no model" for path in sorted(set(found) - set(mapped))]
    problems += [f"{path}: model mapped to a path that is not a schema object" for path in sorted(set(mapped) - set(found))]
    for path in sorted(set(found) & set(mapped)):
        problems += _compare(path, found[path], mapped[path])

    assert not problems, f"{kind} schema and models differ:\n" + "\n".join(problems)


def test_staged_coverage_models_match_upstream_results_schema() -> None:
    found = dict(_objects("upstream-results"))

    problems = [f"{path}: path is not an upstream-results schema object" for path in sorted(UPSTREAM_PATHS - set(found))]
    for path in sorted(UPSTREAM_PATHS & set(found)):
        problems += _compare(path, found[path], UPSTREAM_MODELS[path], partial=path == UPSTREAM_PARTIAL_ROOT)

    assert not problems, "upstream-results schema and models differ:\n" + "\n".join(problems)


def test_comparison_reports_schema_path_and_property() -> None:
    node = {
        "properties": {"id": {}, "extra": {}},
        "required": ["id", "extra"],
        "additionalProperties": False,
    }

    problems = _compare("#/properties/example", node, CAPABILITY)

    assert any("#/properties/example" in p and "extra" in p and "not in the model" in p for p in problems)
    assert any("version" in p and "not in the schema" in p for p in problems)
