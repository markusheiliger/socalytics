"""Segment manifest: the typed segments/segment-manifest 1.0.0 stored next to the materialized segment media."""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import Field

from socalytics_analyst.models.common import ContractModel, ContractVersionV1, Digest, Identifier, Milliseconds, Timestamp

PositiveInt = Annotated[int, Field(ge=1)]


class SegmentIdentity(ContractModel):
    recording_version_id: Identifier
    timeline_mapping_digest: Digest
    segmentation_policy_digest: Digest
    segment_number: PositiveInt


class SegmentationPolicy(ContractModel):
    version: Literal[1]
    duration_seconds: PositiveInt


class HalfOpenInterval(ContractModel):
    start_ms: Milliseconds
    end_ms: PositiveInt


class TimelineSpan(ContractModel):
    media_start_ms: Milliseconds
    media_end_ms: PositiveInt
    match_start_ms: Milliseconds


class MediaObject(ContractModel):
    artifact_reference: Annotated[str, Field(min_length=1, max_length=512)]
    media_type: Literal["video/mp4"]
    digest: Digest
    size_bytes: PositiveInt


class Producer(ContractModel):
    toolchain: Annotated[str, Field(min_length=1, max_length=200)]


class SegmentManifest(ContractModel):
    contract_version: ContractVersionV1
    identity: SegmentIdentity
    segmentation_policy: SegmentationPolicy
    nominal_window: HalfOpenInterval
    requested_source_interval: HalfOpenInterval
    encoded_interval: HalfOpenInterval
    timeline_spans: Annotated[tuple[TimelineSpan, ...], Field(min_length=1)]
    media: MediaObject
    producer: Producer
    created_at: Timestamp
