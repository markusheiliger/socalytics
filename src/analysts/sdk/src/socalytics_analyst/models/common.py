"""Common contract definitions mirrored from contracts/common/v1/common.schema.json and shared result schemas."""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, StringConstraints, model_validator
from pydantic.alias_generators import to_camel

Identifier = Annotated[str, StringConstraints(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9][A-Za-z0-9._\-]*$")]
Key = Annotated[str, StringConstraints(min_length=1, max_length=100, pattern=r"^[a-z0-9][a-z0-9._\-]*$")]
SemanticVersion = Annotated[str, StringConstraints(pattern=r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
ContractVersionV1 = Annotated[str, StringConstraints(pattern=r"^1\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
Timestamp = Annotated[str, StringConstraints(pattern=r"Z$")]
Digest = Annotated[str, StringConstraints(pattern=r"^sha-256:[0-9a-f]{64}$")]
SafeLabel = Annotated[
    str, StringConstraints(min_length=1, max_length=100, pattern=r"^[A-Za-z0-9][A-Za-z0-9._+\-]*$")
]
AttemptNumber = Annotated[int, Field(ge=1)]
Milliseconds = Annotated[int, Field(ge=0)]
ObjectName = Annotated[str, StringConstraints(pattern=r"^[a-z0-9][a-z0-9._\-]{0,127}$")]
ObjectKey = Annotated[
    str, StringConstraints(min_length=1, max_length=1024, pattern=r"^[A-Za-z0-9][A-Za-z0-9._\-/]*$")
]
Traceparent = Annotated[str, StringConstraints(pattern=r"^[0-9a-f]{2}-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$")]


class ContractModel(BaseModel):
    """Frozen, closed base model that reads and writes camelCase names."""

    model_config = ConfigDict(extra="forbid", frozen=True, populate_by_name=True, alias_generator=to_camel)


class CapabilityRef(ContractModel):
    id: Key
    version: SemanticVersion


class Correlation(ContractModel):
    correlation_id: Identifier
    causation_id: Identifier | None = None
    traceparent: Traceparent | None = None


class AnalysisSegmentReference(ContractModel):
    recording_set_version_id: Identifier
    recording_version_id: Identifier
    timeline_mapping_digest: Digest
    segmentation_policy_digest: Digest
    segment_number: Annotated[int, Field(ge=1)]


class AcceptedResultLineage(ContractModel):
    accepted_result_id: Identifier
    logical_job_id: Identifier
    capability: CapabilityRef
    schema_version: SemanticVersion
    manifest_reference: ObjectKey
    manifest_checksum: Digest


class CoverageEntry(ContractModel):
    segment_id: Identifier | None = None
    match: Literal[True] | None = None
    state: Literal["accepted", "missing", "failed", "blocked", "skipped"]

    @model_validator(mode="after")
    def _exactly_one_subject(self) -> CoverageEntry:
        if (self.segment_id is None) == (self.match is None):
            raise ValueError("exactly one of segmentId or match must be set")
        return self
