"""Staged coverage.json: the upstream and coverage parts of upstream-results 1.0.0 and 1.1.0."""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import Field

from socalytics_analyst.models.common import CapabilityRef, ContractModel, CoverageEntry, SemanticVersion


class CoveragePolicy(ContractModel):
    """Coverage policy of the dependency edge (1.1.0); an absent policy means complete."""

    mode: Literal["complete", "partial"]
    minimum_accepted_fraction: Annotated[float, Field(gt=0, le=1)] | None = None


class StagedUpstream(ContractModel):
    capability: CapabilityRef
    schema_version: SemanticVersion
    edge_kind: Literal["required", "optional", "conditional"]
    coverage: CoveragePolicy | None = None


class StagedCoverage(ContractModel):
    upstream: StagedUpstream
    coverage: Annotated[list[CoverageEntry], Field(max_length=1024)]
