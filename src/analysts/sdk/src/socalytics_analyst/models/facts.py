"""Fact artifact mirrored from contracts/analysis/fact-artifact/v1."""

from __future__ import annotations

from typing import Annotated, Any, Final, Literal

from pydantic import Field

from socalytics_analyst.models.common import CapabilityRef, ContractModel, ContractVersionV1, Key, SemanticVersion

FACTS_MEDIA_TYPE: Final = "application/vnd.socalytics.facts+json"


class Fact(ContractModel):
    """One fact with a half-open match-time range; the platform additionally checks end_ms > start_ms."""

    kind: Key
    start_ms: Annotated[int, Field(ge=0)]
    end_ms: Annotated[int, Field(ge=1)]
    confidence: Annotated[float, Field(ge=0, le=1)]
    payload: dict[str, Any]


class FactArtifact(ContractModel):
    contract_version: ContractVersionV1
    capability: CapabilityRef
    schema_version: SemanticVersion
    output_type: Literal["foundational-facts", "soccer-concepts"]
    facts: list[Fact]
