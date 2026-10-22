# Data Model: Materialized Video Segments and Analyst Input Staging

Persistence lives in the `socalytics` schema; the migration is one forward-only file
`NNNN_segments_materialized_segments.sql`, numbered at implementation time as the next free number.

## Materialized Segment (`materialized_segments`, versioned)

| Field | Type | Rules |
| --- | --- | --- |
| `id` | uuid | Primary key |
| `recording_version_id` | uuid | Identity; references the recording version |
| `timeline_mapping_digest` | text | Identity; `sha-256:<hex>` of an immutable mapping of that recording version |
| `segmentation_policy_digest` | text | Identity; `sha-256:<hex>` |
| `segment_number` | integer | Identity; `> 0` |
| `policy_version` | integer | Bound on first validated request; `1` is the only supported version |
| `duration_seconds` | integer | Bound on first validated request; `> 0` |
| `state` | text | `absent`, `materializing`, `available`, `failed` |
| `demanded` | boolean | `true` while a request wants the segment materialized; cleared by purge |
| `candidate_id` | uuid null | The in-flight candidate while `materializing` |
| `lease_owner` | text null | Worker instance identity while `materializing` |
| `lease_expires_at` | timestamptz null | Lease expiry while `materializing` |
| `attempts_used` | integer | Materialization attempts in the current demand cycle; reset by a new demand after `failed` |
| `next_attempt_at` | timestamptz null | Earliest retry time after a transient failure |
| `failure_reason` | text null | Sanitized reason when `failed` |
| `media_object_key` | text null | Current association; set only when `available` |
| `manifest_object_key` | text null | Current association; set only when `available` |
| `media_digest` | text null | `sha-256:<hex>` of the stored media |
| `media_size_bytes` | bigint null | Stored media size |
| `manifest_checksum` | text null | `sha-256:<hex>` of the stored manifest bytes |
| `created_at`, `updated_at`, `available_at`, `purged_at` | timestamptz | Lifecycle timestamps |
| `version` | bigint | Managed by `socalytics.attach_version_trigger` |

Constraints: unique `(recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number)`;
association columns are all null unless `state = 'available'`, and all non-null when it is; `candidate_id`,
`lease_owner`, and `lease_expires_at` are non-null exactly when `state = 'materializing'`.

### State transitions

```text
absent ──demand──▶ absent(demanded) ──claim──▶ materializing
materializing ──publish (candidate owns lease, digest verified)──▶ available
materializing ──transient failure, attempts left──▶ absent(demanded, next_attempt_at)
materializing ──non-retryable failure or budget exhausted──▶ failed
materializing ──lease expired──▶ reclaimed by a worker (old candidate objects deleted)
failed ──new demand──▶ absent(demanded, attempts_used = 0)
available ──digest check fails on use──▶ failed (segment-media-invalid); its objects are deleted in the same step
available | failed ──purge (not in use)──▶ absent(not demanded, association cleared, objects deleted)
```

Sanitized failure reasons: `segment-coverage-empty`, `segment-coverage-incomplete`, `segmentation-policy-unknown`,
`segmentation-policy-conflict`, `segment-scope-mismatch`, `source-unavailable`, `toolchain-unavailable`,
`extraction-failed`, `storage-unavailable`, `segment-too-large`, `segment-media-invalid`, `retry-budget-exhausted`.
Rejections before publication (`segment-coverage-empty`, `segmentation-policy-*`, `segment-scope-mismatch`, missing or
nonpositive fields) change no row, never create a row in `available`, and never become lineage; the rejected job
records the reason and readiness treats it like a `failed` segment for that job only. A `materializing` row is always
in use for purge.

## Segment Association (value)

`{ mediaObjectKey, manifestObjectKey, mediaDigest, mediaSizeBytes, manifestChecksum }` - immutable once published;
object keys are `segments/{recordingVersionId}/{mappingDigestHex}/{policyDigestHex}/{segmentNumber}/{candidateId}/`
followed by `segment.mp4` or `segment-manifest.json`. Keys never appear in member responses or Analyst mounts.

## Segment Manifest (document, `contracts/segments/segment-manifest/v1/` 1.0.0)

| Field | Content |
| --- | --- |
| `contractVersion` | `1.0.0` |
| `identity` | `recordingVersionId`, `timelineMappingDigest`, `segmentationPolicyDigest`, `segmentNumber` |
| `segmentationPolicy` | `version`, `durationSeconds` |
| `nominalWindow` | `{ startMs, endMs }` match time, half-open |
| `requestedSourceInterval` | `{ startMs, endMs }` media time, half-open |
| `encodedInterval` | `{ startMs, endMs }` media time of the first and after the last encoded video packet; covers the requested interval |
| `timelineSpans` | `[{ mediaStartMs, mediaEndMs, matchStartMs }]` intersected with the nominal window, ordered |
| `media` | `{ artifactReference, mediaType: "video/mp4", digest: "sha-256:…", sizeBytes }` |
| `producer` | `{ toolchain: "<ffmpeg version string>" }` |
| `createdAt` | UTC timestamp |

The manifest checksum is the SHA-256 of the stored manifest bytes; the media digest is the SHA-256 of the stored media.

## Logical Job additions

- Node snapshot JSON gains `videoPolicy` (`required`, `optional`, `forbidden`) from the pinned Analyst profile.
- Job input snapshot JSON gains `segmentMedia` `{ manifestChecksum, manifestReference, mediaDigest, mediaSizeBytes }`
  when the job became ready with media; absent otherwise.
- `logical_jobs` gains the nullable column `segment_rejection_reason` (text), set at run creation when the job's
  materialization request was rejected; readiness then treats the job's segment as `failed`.
- New logical-job failure reason `segment-unavailable`.

## Staged Segment Media (Manager, attempt directory)

```text
{attemptDirectory}/media/              → /socalytics/media/ (read-only)
  segment-manifest.json                 # verified against segmentMedia.manifestChecksum and the schema
  segment.mp4                           # verified against the manifest's media digest and size
```

## Segment Purge (audit event)

Security audit event `segments.purged` with actor, club, match, purged count, in-use count, and correlation; refused
listing or purge attempts and refused segment-media reads are audited with the existing refusal reasons.
