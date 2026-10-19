# Run Status Read: Node Input Coverage

Operation: the existing member read `GET /api/v1/analysis/runs/{runId}` (policy `ActiveMember`, team-scoped through
`IAccessAuthorizer`); authorization, status codes, and problem responses are unchanged.

Additive change to `AnalysisNodeStatusDto`:

```json
{
  "inputCoverage": [
    {
      "upstreamNodeKey": "dev-segment-node",
      "coverageMode": "partial",
      "minimumAcceptedFraction": null,
      "accepted": 3,
      "failed": 1,
      "blocked": 0,
      "skipped": 0,
      "missing": 0
    }
  ]
}
```

- `inputCoverage` is `null` for nodes without incoming edges and for nodes none of whose jobs has an input snapshot
  yet; it never shows inferred values.
- One item per incoming edge, ordered by `upstreamNodeKey`; counts are summed over the node's jobs that have an input
  snapshot (for a match-scoped node: its single job).
- `coverageMode` is `complete` or `partial`; `minimumAcceptedFraction` is a number or `null`.
- The OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` is refreshed with
  `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`
  outside the verification environment; the Web UI's generated client picks the field up at build time without UI
  changes.
