"""Manifest models: the result manifest (1.0.0 and 1.1.0; 1.1.0 adds the optional inputCoverage) and the Analyst
manifest (1.0.0 to 1.2.0; 1.2.0 adds `output.factPayloadSchemas`)."""

from __future__ import annotations

import json
from typing import Annotated, Any, Literal

from pydantic import Field, StringConstraints, field_validator, model_validator

from socalytics_analyst.models.common import (
    AttemptNumber,
    CapabilityRef,
    ContractModel,
    ContractVersionV1,
    Digest,
    Identifier,
    Key,
    Milliseconds,
    ObjectName,
    SafeLabel,
    SemanticVersion,
)

MediaType = Annotated[
    str, StringConstraints(max_length=100, pattern=r"^[a-z0-9][a-z0-9.+\-]*/[a-z0-9][a-z0-9.+\-]*$")
]
Confidence = Annotated[float, Field(ge=0, le=1)]


class ManifestSegment(ContractModel):
    segment_id: Identifier
    window_start_ms: Milliseconds
    window_end_ms: Milliseconds


class ManifestScope(ContractModel):
    team_id: Identifier
    match_id: Identifier
    recording_set_version_id: Identifier
    run_id: Identifier
    node_id: Identifier
    logical_job_id: Identifier
    segment: ManifestSegment | None = None
    match: Literal[True] | None = None

    @model_validator(mode="after")
    def _exactly_one_scope(self) -> ManifestScope:
        if (self.segment is None) == (self.match is None):
            raise ValueError("exactly one of segment or match must be set")
        return self


class ProfileRef(ContractModel):
    id: Identifier
    digest: Digest


class PolicyVersion(ContractModel):
    id: Key
    version: SafeLabel


class WorkflowRef(ContractModel):
    id: Identifier
    version: SafeLabel


class Provenance(ContractModel):
    capability: CapabilityRef
    schema_version: SemanticVersion
    analyst_profile: ProfileRef
    image_digest: Digest
    model_digest: Digest | None = None
    policy_versions: Annotated[list[PolicyVersion], Field(max_length=32)]
    workflow: WorkflowRef


class ManifestAttempt(ContractModel):
    attempt_id: Identifier
    attempt_number: AttemptNumber


class ManifestArtifact(ContractModel):
    name: ObjectName
    digest: Digest
    size_bytes: Annotated[int, Field(ge=1)]
    media_type: MediaType


class ValidityWindow(ContractModel):
    start_ms: Milliseconds
    end_ms: Milliseconds


class UpstreamLineageEntry(ContractModel):
    accepted_result_id: Identifier
    capability: CapabilityRef
    schema_version: SemanticVersion
    manifest_checksum: Digest


class InputCoverageUnit(ContractModel):
    segment_id: Identifier | None = None
    match: Literal[True] | None = None
    state: Literal["accepted", "missing", "failed", "blocked", "skipped"]

    @model_validator(mode="after")
    def _exactly_one_subject(self) -> InputCoverageUnit:
        if (self.segment_id is None) == (self.match is None):
            raise ValueError("exactly one of segmentId or match must be set")
        return self


class InputCoverage(ContractModel):
    capability: CapabilityRef
    schema_version: SemanticVersion
    units: Annotated[list[InputCoverageUnit], Field(max_length=1024)]


class ResultManifest(ContractModel):
    contract_version: ContractVersionV1
    scope: ManifestScope
    provenance: Provenance
    attempt: ManifestAttempt
    artifacts: Annotated[list[ManifestArtifact], Field(max_length=256)]
    validity_window: ValidityWindow
    fact_count: Annotated[int, Field(ge=0)]
    upstream_lineage: list[UpstreamLineageEntry]
    output_type: Literal["foundational-facts", "soccer-concepts"]
    confidence: Confidence
    evidence_references: list[Identifier]
    input_coverage: Annotated[list[InputCoverage], Field(max_length=64)] | None = None


MAX_PAYLOAD_KINDS = 64
MAX_PAYLOAD_SCHEMA_BYTES = 16 * 1024
MAX_PAYLOAD_SCHEMA_DEPTH = 8
MAX_PAYLOAD_SCHEMA_NODES = 256

PAYLOAD_SCHEMAS_TOO_MANY_KINDS = "payload-schemas-too-many-kinds"
PAYLOAD_SCHEMA_INVALID_KIND = "payload-schema-invalid-kind"
PAYLOAD_SCHEMA_TOO_LARGE = "payload-schema-too-large"
PAYLOAD_SCHEMA_ROOT_NOT_OBJECT = "payload-schema-root-not-object"
PAYLOAD_SCHEMA_NOT_A_SCHEMA = "payload-schema-not-a-schema"
PAYLOAD_SCHEMA_UNKNOWN_KEYWORD = "payload-schema-unknown-keyword"
PAYLOAD_SCHEMA_EXTERNAL_REF = "payload-schema-external-ref"
PAYLOAD_SCHEMA_UNRESOLVED_REF = "payload-schema-unresolved-ref"
PAYLOAD_SCHEMA_CYCLIC_REF = "payload-schema-cyclic-ref"
PAYLOAD_SCHEMA_TOO_DEEP = "payload-schema-too-deep"
PAYLOAD_SCHEMA_TOO_MANY_NODES = "payload-schema-too-many-nodes"

_REF_PREFIX = "#/$defs/"
_KEYWORDS = frozenset(
    {
        "type", "enum", "const", "properties", "required", "additionalProperties", "items", "minItems",
        "maxItems", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength",
        "$defs", "$ref", "title", "description",
    }
)  # fmt: skip


def _is_object(value: Any) -> bool:
    return isinstance(value, dict)


def _walk_payload_schema(schema: Any, depth: int, state: dict[str, int], refs: list[str]) -> str | None:
    if depth > MAX_PAYLOAD_SCHEMA_DEPTH:
        return PAYLOAD_SCHEMA_TOO_DEEP
    state["nodes"] += 1
    if state["nodes"] > MAX_PAYLOAD_SCHEMA_NODES:
        return PAYLOAD_SCHEMA_TOO_MANY_NODES
    if not _is_object(schema):
        return PAYLOAD_SCHEMA_NOT_A_SCHEMA

    for name, value in schema.items():
        if name not in _KEYWORDS:
            return PAYLOAD_SCHEMA_UNKNOWN_KEYWORD
        children: list[Any] = []
        if name == "$ref":
            target = value
            if (
                not isinstance(target, str)
                or not target.startswith(_REF_PREFIX)
                or len(target) == len(_REF_PREFIX)
                or "/" in target[len(_REF_PREFIX) :]
            ):
                return PAYLOAD_SCHEMA_EXTERNAL_REF
            refs.append(target[len(_REF_PREFIX) :])
        elif name == "items" or (name == "additionalProperties" and not isinstance(value, bool)):
            children = [value]
        elif name in ("properties", "$defs"):
            if not _is_object(value):
                return PAYLOAD_SCHEMA_NOT_A_SCHEMA
            children = list(value.values())
        for child in children:
            code = _walk_payload_schema(child, depth + 1, state, refs)
            if code is not None:
                return code
    return None


def _collect_refs(schema: Any) -> list[str]:
    refs: list[str] = []
    stack = [schema]
    while stack:
        current = stack.pop()
        if not _is_object(current):
            continue
        for name, value in current.items():
            if name == "$ref" and isinstance(value, str):
                refs.append(value[len(_REF_PREFIX) :])
            elif name in ("properties", "$defs") and _is_object(value):
                stack.extend(value.values())
            elif name in ("items", "additionalProperties"):
                stack.append(value)
    return refs


def _has_cycle(defs: dict[str, Any]) -> bool:
    edges = {name: _collect_refs(value) for name, value in defs.items()}
    state: dict[str, int] = {}

    def visit(name: str) -> bool:
        if name in state:
            return state[name] == 1
        state[name] = 1
        for target in edges[name]:
            if target in edges and visit(target):
                return True
        state[name] = 2
        return False

    return any(visit(name) for name in edges)


def _check_payload_schema(kind: str, schema: Any) -> str | None:
    if not 1 <= len(kind) <= 100:
        return PAYLOAD_SCHEMA_INVALID_KIND
    canonical = json.dumps(schema, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    if len(canonical.encode("utf-8")) > MAX_PAYLOAD_SCHEMA_BYTES:
        return PAYLOAD_SCHEMA_TOO_LARGE
    if not _is_object(schema):
        return PAYLOAD_SCHEMA_NOT_A_SCHEMA
    if schema.get("type") != "object":
        return PAYLOAD_SCHEMA_ROOT_NOT_OBJECT

    root_defs = schema.get("$defs")
    defs = dict(root_defs) if _is_object(root_defs) else {}
    refs: list[str] = []
    code = _walk_payload_schema(schema, 1, {"nodes": 0}, refs)
    if code is not None:
        return code
    if any(target not in defs for target in refs):
        return PAYLOAD_SCHEMA_UNRESOLVED_REF
    return PAYLOAD_SCHEMA_CYCLIC_REF if _has_cycle(defs) else None


def check_fact_payload_schemas(schemas: dict[str, Any]) -> tuple[str, str] | None:
    """Return the first refusal as (code, kind), or None; same rules, order, and codes as the platform Domain."""
    if len(schemas) > MAX_PAYLOAD_KINDS:
        return PAYLOAD_SCHEMAS_TOO_MANY_KINDS, ""
    for kind in sorted(schemas):
        code = _check_payload_schema(kind, schemas[kind])
        if code is not None:
            return code, kind
    return None


class PayloadSchema(ContractModel):
    """The closed payload schema subset: any other keyword is refused."""

    type: Literal["object", "array", "string", "number", "integer", "boolean", "null"] | None = None
    enum: Annotated[list[Any], Field(min_length=1)] | None = None
    const: Any = None
    properties: dict[str, PayloadSchema] | None = None
    required: list[str] | None = None
    additional_properties: bool | PayloadSchema | None = None
    items: PayloadSchema | None = None
    min_items: Annotated[int, Field(ge=0)] | None = None
    max_items: Annotated[int, Field(ge=0)] | None = None
    minimum: float | None = None
    maximum: float | None = None
    exclusive_minimum: float | None = None
    exclusive_maximum: float | None = None
    min_length: Annotated[int, Field(ge=0)] | None = None
    max_length: Annotated[int, Field(ge=0)] | None = None
    defs: dict[str, PayloadSchema] | None = Field(default=None, alias="$defs")
    ref: Annotated[str, StringConstraints(pattern=r"^#/\$defs/[A-Za-z0-9_.-]+$")] | None = Field(
        default=None, alias="$ref"
    )
    title: str | None = None
    description: str | None = None

    @field_validator("required")
    @classmethod
    def _required_unique(cls, value: list[str] | None) -> list[str] | None:
        if value is not None and len(set(value)) != len(value):
            raise ValueError("required entries must be unique")
        return value


class AnalystCapability(ContractModel):
    id: Key
    version: SemanticVersion
    tier: Literal["low-level", "high-level"]
    execution_scope: Literal["segment", "match"]


class UpstreamCoverage(ContractModel):
    mode: Literal["partial"]
    minimum_accepted_fraction: Annotated[float, Field(gt=0, le=1)] | None = None


class UpstreamDeclaration(ContractModel):
    capability_id: Key
    capability_version: SemanticVersion
    schema_version: SemanticVersion
    kind: Literal["required", "optional", "conditional"]
    coverage: UpstreamCoverage | None = None


class AnalystOutput(ContractModel):
    schema_version: SemanticVersion
    fact_payload_schemas: Annotated[dict[Key, PayloadSchema], Field(max_length=MAX_PAYLOAD_KINDS)] | None = None

    @model_validator(mode="before")
    @classmethod
    def _check_payload_schemas(cls, data: Any) -> Any:
        schemas = data.get("factPayloadSchemas", data.get("fact_payload_schemas")) if _is_object(data) else None
        if _is_object(schemas):
            refusal = check_fact_payload_schemas(schemas)
            if refusal is not None:
                code, kind = refusal
                raise ValueError(f"{code}: {kind}" if kind else code)
        return data


class AnalystModel(ContractModel):
    digest: Digest


class AnalystRuntime(ContractModel):
    constraints: list[SafeLabel]
    accelerators: list[SafeLabel]


class AnalystRequirements(ContractModel):
    min_max_attempts: Annotated[int, Field(ge=1)]
    min_lease_seconds: Annotated[int, Field(ge=1)]
    min_stale_timeout_seconds: Annotated[int, Field(ge=1)]
    min_execution_timeout_seconds: Annotated[int, Field(ge=1)]


class AnalystDeterminism(ContractModel):
    deterministic: bool
    idempotent: bool


class AnalystManifest(ContractModel):
    """The analyst-manifest.json of an Analyst image, manifest 1.0.0 to 1.2.0."""

    manifest_version: Literal["1.0.0", "1.1.0", "1.2.0"]
    capability: AnalystCapability
    upstream: list[UpstreamDeclaration]
    video_access: Literal["required", "optional", "forbidden"]
    output: AnalystOutput
    compatible_profiles: list[Key]
    model: AnalystModel
    runtime: AnalystRuntime
    resources: dict[str, str]
    requirements: AnalystRequirements
    determinism: AnalystDeterminism

    @model_validator(mode="after")
    def _payload_schemas_need_1_2_0(self) -> AnalystManifest:
        if self.output.fact_payload_schemas is not None and self.manifest_version != "1.2.0":
            raise ValueError("output.factPayloadSchemas requires manifestVersion 1.2.0")
        return self
