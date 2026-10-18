# Contract: Manager Segment Media Read

`GET /api/v1/analysis/attempts/{attemptId}/segment-media?fencingToken={token}` - operation `getAnalysisSegmentMedia`,
mapped beside `listAnalysisUpstreamResults` in `AnalystManagerEndpoints` with the same Manager authorization (DPoP-bound
Manager token, Active registration) and the same problem-response conventions. It appears in the OpenAPI document; the
committed OpenAPI snapshot is refreshed.

## Checks, in order

1. The attempt exists (`404` otherwise) and belongs to the calling Manager (`403`, audited as foreign).
2. `fencingToken` is present (`400` `fencingToken: required`), the attempt is `Active`, its lease has not expired, the
   token matches, and it is the job's current attempt (`409` `attempt-obsolete`).
3. The job's input snapshot pins `segmentMedia` (`409` `segment-media-not-applicable`, audited as out of scope). Jobs
   whose pinned video policy is `forbidden` never have it.
4. The media object's attested full-object SHA-256 and size equal the pinned values and the stored manifest's SHA-256
   equals the pinned checksum (`409` `segment-media-invalid`; the materialized segment becomes `failed` and its objects
   are deleted).

## Response `200`

A document valid against `analysis/segment-media` 1.0.0: the attempt and job identity, the pinned manifest checksum,
the manifest document, and two presigned GET grants (`segment-manifest.json`, `segment.mp4`) expiring at the earlier of
`Analysis:Results:GrantLifetime` (the existing result grant lifetime option) and the attempt's lease expiry. No grant
permits writing, listing, or other objects. The response never contains source-recording locations.

## Manager behavior

Before launching an attempt whose job message carries `segmentMedia`, the Manager calls the operation, renewing the
attempt lease as for upstream staging, downloads both objects with the grants, verifies:

- the manifest's SHA-256 equals `segmentMedia.manifestChecksum`, and the manifest is valid against the segment
  manifest schema;
- the media's SHA-256 and size equal `manifest.media.digest` and `manifest.media.sizeBytes`;
- media plus staged upstream bytes stay within `Work:Staging:MaxBytesPerAttempt`;

and writes them to `{attemptDirectory}/media/` as described in [container-interface.md](container-interface.md).
Refusals and failures end the attempt as failed with the existing staging categories; the container is never launched
with partial media.
