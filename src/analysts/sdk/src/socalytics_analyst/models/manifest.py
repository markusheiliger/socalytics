"""Result manifest model for result-manifest 1.0.0 and 1.1.0 (1.1.0 adds the optional inputCoverage)."""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import Field, StringConstraints, model_validator

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
