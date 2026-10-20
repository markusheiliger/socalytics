"""Logical Job: the typed analyst-job 1.0.0 and 1.1.0 ready-work message."""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import Field, model_validator

from socalytics_analyst.models.common import (
    AcceptedResultLineage,
    AnalysisSegmentReference,
    CapabilityRef,
    ContractModel,
    ContractVersionV1,
    Correlation,
    Digest,
    Identifier,
    Key,
    Milliseconds,
    SemanticVersion,
    Timestamp,
)

PositiveInt = Annotated[int, Field(ge=1)]


class InputEntry(ContractModel):
    upstream_node_key: Key
    capability: CapabilityRef
    edge_kind: Literal["required", "optional", "conditional"]
    status: Literal["accepted", "absent", "failed", "not-applicable"]
    accepted_results: tuple[AcceptedResultLineage, ...]

    @model_validator(mode="after")
    def _status_rules(self) -> InputEntry:
        if self.status == "accepted":
            if not self.accepted_results:
                raise ValueError("an accepted input must list at least one accepted result")
        elif self.accepted_results:
            raise ValueError("only an accepted input may list accepted results")
        if self.edge_kind == "required" and self.status != "accepted":
            raise ValueError("a required input must be accepted")
        return self


class PolicyPin(ContractModel):
    policy_id: Key
    version: SemanticVersion


class Pinned(ContractModel):
    analyst_profile_id: Key
    analyst_profile_digest: Digest
    image_digest: Digest
    model_digest: Digest | None
    policies: tuple[PolicyPin, ...]


class Resources(ContractModel):
    cpu_millicores: PositiveInt
    memory_mebibytes: PositiveInt
    scratch_disk_mebibytes: Milliseconds


class AttemptBudget(ContractModel):
    max_attempts: PositiveInt


class Timeouts(ContractModel):
    lease_seconds: PositiveInt
    stale_timeout_seconds: PositiveInt
    execution_timeout_seconds: PositiveInt


class WorkflowRef(ContractModel):
    id: Key
    version: SemanticVersion


class SegmentWindow(ContractModel):
    segment_id: Identifier
    start_ms: Milliseconds
    end_ms: PositiveInt


class LogicalJob(ContractModel):
    contract_version: ContractVersionV1
    message_id: Identifier
    readiness_occurrence_id: Identifier
    readiness_sequence: PositiveInt
    run_id: Identifier
    node_id: Identifier
    node_key: Key
    logical_job_id: Identifier
    team_id: Identifier
    match_id: Identifier
    recording_set_version_id: Identifier
    capability: CapabilityRef
    tier: Literal["low-level", "high-level"]
    schema_version: SemanticVersion
    execution_scope: Literal["segment", "match"]
    segment: AnalysisSegmentReference | None = None
    segment_window: SegmentWindow | None = None
    workflow: WorkflowRef | None = None
    inputs: tuple[InputEntry, ...]
    pinned: Pinned
    resources: Resources
    attempt_budget: AttemptBudget
    timeouts: Timeouts
    created_at: Timestamp
    correlation: Correlation

    @model_validator(mode="after")
    def _scope_rules(self) -> LogicalJob:
        if self.execution_scope == "segment":
            if self.segment is None:
                raise ValueError("a segment job must carry segment")
        else:
            if self.segment is not None:
                raise ValueError("a match job must not carry segment")
            if self.segment_window is not None:
                raise ValueError("a match job must not carry segmentWindow")
        return self
