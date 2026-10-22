# Contract: Member Operations for Materialized Segments

Both operations use `MapMemberApi()` (policy `ActiveMember`, `SessionAntiforgeryFilter` for the unsafe method),
`ProblemResults`, and `IAccessAuthorizer.AuthorizeClubAsync` with the Club Admin permission of the club that owns the
match. A match the caller cannot see returns `404`; a visible match without the Club Admin permission returns `403`;
neither discloses segments. Both appear in the OpenAPI document; the committed snapshot is refreshed.

## `GET /api/v1/matches/{matchId}/materialized-segments` - `listMaterializedSegments`

Returns every materialized segment row whose recording version belongs to one of the match's finalized recording sets,
ordered by recording version, mapping digest, policy digest, and segment number:

```json
{
  "matchId": "…",
  "segments": [
    {
      "recordingVersionId": "…",
      "timelineMappingDigest": "sha-256:…",
      "segmentationPolicyDigest": "sha-256:…",
      "segmentNumber": 1,
      "state": "available",
      "mediaSizeBytes": 1234567,
      "failureReason": null,
      "inUse": false,
      "availableAt": "2026-10-17T12:00:00.000Z"
    }
  ]
}
```

`state` is one of `absent`, `materializing`, `available`, `failed`; `mediaSizeBytes` is present only for `available`;
`inUse` is true when a `Waiting`, `Ready`, or `Claimed` job of a non-terminal run pins or awaits the segment, or the
segment is `materializing`. No object
key, URL, or digest of the media is returned.

## `POST /api/v1/matches/{matchId}/materialized-segments/purge` - `purgeMaterializedSegments`

No request body. Purges every listed segment that is not in use: deletes its media and manifest objects, sets it to
`absent` without demand, clears its association and failure, and keeps its identity. Returns `200`:

```json
{ "matchId": "…", "purged": 3, "inUse": 1 }
```

Repeating the call purges nothing further and returns `purged: 0`. One security audit event `segments.purged` is written
per call with the actor, club, match, and counts. Storage unavailability returns `503` and leaves unpurged rows unchanged;
rows already purged in the call stay purged.
