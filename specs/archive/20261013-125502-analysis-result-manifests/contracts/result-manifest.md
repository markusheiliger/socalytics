# Contract: Result Manifest v1

Location: `contracts/analysis/result-manifest/v1/` (JSON Schema 2020-12),
following the repository's contract layout:

- `result-manifest.schema.json` (`$id` per the contracts index convention) and
  `releases/1.0.0.schema.json`
- `examples/valid/segment-result.json`, `examples/valid/match-result-with-lineage.json`
- `examples/invalid/` with `expectations.json`: missing scope, unknown
  property, presigned URL in an artifact name, credential field, inline media,
  negative fact count, confidence above 1, missing `contractVersion`
- an index row in `contracts/README.md`

## Shape (summary)

| Property | Content |
| --- | --- |
| `contractVersion` | `"1.0.0"` |
| `scope` | `teamId`, `matchId`, `recordingSetVersionId`, `runId`, `nodeId`, `logicalJobId`; exactly one of `segment` (`segmentId`, `windowStartMs`, `windowEndMs`, half-open) or `match` (`true`) |
| `provenance` | `capability` (`id`, `version`), `schemaVersion`, `analystProfile` (`id`, `digest`), `imageDigest`, `modelDigest` (optional), `policyVersions`, `workflow` (`id`, `version`) |
| `attempt` | `attemptId`, `attemptNumber` |
| `artifacts` | 0–n `{ name, digest, sizeBytes, mediaType }`; names as in the grants API |
| `validityWindow` | `{ startMs, endMs }`, half-open, match time |
| `factCount` | integer ≥ 0 |
| `upstreamLineage` | 0–n `{ acceptedResultId, capability: { id, version }, schemaVersion, manifestChecksum }` |
| `outputType` | `foundational-facts` or `soccer-concepts` |
| `confidence` | number 0–1 |
| `evidenceReferences` | 0–n identifier strings (no URLs) |

Digests use the shared `sha-256:<64 lowercase hex>` definition from
`contracts/common/v1/`. `additionalProperties: false` at every level.

## Consumers

- Platform: embedded through the existing `contracts/**/*.schema.json`
  resource pattern and validated by `JsonSchemaContractValidator` during
  acceptance.
- Analyst Manager: embedded the same way in Manager Core and validated by
  `ResultPublisher` before grants are requested.
- Both solutions' tests validate every example against its expectation
  (SC-006).
