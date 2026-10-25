# Contract: Segment Adapter

`src/clients/web/src/platform/adapters/segmentAdapter.ts`, an application-owned adapter over the generated Kiota client
(like `operationsAdapter`). It consumes the existing platform operations; it defines no platform contract.

```ts
interface SegmentAdapter {
  /** listMaterializedSegments: GET /api/v1/matches/{matchId}/materialized-segments */
  list(matchId: string): Promise<SegmentList>;
  /** purgeMaterializedSegments: POST /api/v1/matches/{matchId}/materialized-segments/purge (no body, no If-Match) */
  purge(matchId: string): Promise<PurgeOutcome>;
}
```

| Operation | Success | Refusals mapped through `toPlatformFailure` |
| --- | --- | --- |
| `list` | `200` `MaterializedSegmentListDto` | `401` session ended, `403` notPermitted, `404` not found |
| `purge` | `200` `SegmentPurgeResultDto` | `401`, `403`, `404`, `503` temporarily unavailable (segments already purged by the call stay purged) |

Rules:

- Both calls go through the shared platform client, so the anti-forgery header, session outcome, connectivity, and
  version middleware apply; the adapter adds no headers.
- Nullable DTO fields become `undefined`; an unknown `state` string is kept verbatim.
- The adapter never retries; `purge` is sent at most once per confirmation.
- No endpoint, schema, or OpenAPI snapshot change; the operations and schemas are already pinned by the committed
  OpenAPI snapshot.
