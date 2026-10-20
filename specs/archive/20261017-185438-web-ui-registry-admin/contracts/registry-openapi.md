# Contract: Registry OpenAPI Schemas

The 13 Registry operations of
[Analyst Registry → Registry API](../../20261012-152148-analyst-registry/contracts/registry-api.md)
keep their paths, methods, operation ids, statuses, problem codes, headers,
authorization, and JSON bodies. This feature only describes their bodies in
the OpenAPI document (research.md § R1), so the Web UI's generated client has
models for them.

## Request schemas (`Accepts`, `application/json`)

| Operation | Schema | Properties |
| --- | --- | --- |
| `registerAnalystImage` | `RegisterAnalystImageRequest` | `reference` |
| `createAnalystProfile` | `CreateAnalystProfileRequest` | `profileId`, `imageDigest`, `content` (`AnalystProfileContent`) |
| `defineWorkflow` | `DefineWorkflowRequest` | `workflowId`, `workflowVersion`, `requestedCapabilities[]`, `completionPolicy`, `segmentationPolicy` (`version`, `durationSeconds`, `digest`), `nodes[]` (`nodeKey`, `profileId`, `profileVersion`), `edges[]` (`upstreamNodeKey`, `dependentNodeKey`, `kind`, `predicate?` with `kind`, `capabilityId?`, `nodeKey?`) |
| `setDefaultWorkflow` | `SetDefaultWorkflowRequest` | `workflowId`, `workflowVersion` |

`AnalystProfileContent`: `capabilityId`, `capabilityVersion`, `modelDigest`,
`runtimeConstraints[]`, `accelerators[]`, `resources` (string map),
`maxAttempts`, `leaseSeconds`, `staleTimeoutSeconds`,
`executionTimeoutSeconds`, `videoPolicy`, `inputSchemaVersions` (string map),
`outputSchemaVersion`. The request records replace today's internal body
records (`RegisterAnalystImageBody`, `CreateAnalystProfileBody`,
`DefineWorkflowBody`, `SetDefaultWorkflowBody`) with the same property names;
malformed JSON keeps today's handling (`null` body → validation failure).

## Response schemas (`Produces`)

| Operation | Status | Schema |
| --- | --- | --- |
| `registerAnalystImage` | `201`, `200` | `AnalystImageDto` |
| `listAnalystImages` | `200` | `AnalystImageListDto` (`items[]`: `imageDigest`, `capabilityId`, `capabilityVersion`, `registeredAt`) |
| `getAnalystImage` | `200` | `AnalystImageDto` |
| `createAnalystProfile` | `201`, `200` | `AnalystProfileDto` |
| `listAnalystProfiles` | `200` | `AnalystProfileListDto` (`items[]`: `AnalystProfileDto`) |
| `getAnalystProfile`, `approveAnalystProfile`, `retireAnalystProfile` | `200` | `AnalystProfileDto` |
| `defineWorkflow` | `201` | `WorkflowDefinitionDto` |
| `listWorkflows` | `200` | `WorkflowListDto` (`items[]`: `workflowId`, `workflowVersion`, `definitionDigest`, `createdAt`) |
| `getWorkflow` | `200` | `WorkflowDefinitionDto` |
| `getDefaultWorkflow`, `setDefaultWorkflow` | `200` | `DefaultWorkflowDto` |

- `AnalystImageDto`: `imageDigest`, `repository`, `manifestDigest`,
  `capability` (`id`, `version`, `tier`, `executionScope`), `modelDigest`,
  `manifest` (free-form JSON object: the stored `analyst-manifest.json`,
  governed by `contracts/analysis/analyst-manifest/`), `source`,
  `registeredAt`, `registeredBy`.
- `AnalystProfileDto`: `profileId`, `version`, `profileDigest`, `imageDigest`,
  `state`, `content` (`AnalystProfileContent`), `createdAt`, `createdBy`,
  `approvedAt?`, `approvedBy?`, `retiredAt?`, `retiredBy?`.
- `WorkflowDefinitionDto`: `workflowId`, `workflowVersion`,
  `definitionDigest`, `definition` (`requestedCapabilities[]`,
  `completionPolicy`, `segmentationPolicy` with `version`, `durationSeconds`,
  `digest`, `nodes[]` with `nodeKey`, `profileId`, `profileVersion`,
  `profileDigest`, `edges[]` as in the request), `createdAt`, `createdBy`.
- `DefaultWorkflowDto`: `workflowId`, `workflowVersion`, `changedAt`,
  `changedBy`.

## Problems (`ProducesProblem`)

Each operation declares the problem statuses it already returns: `400`, `401`,
`403`; plus `404` for the single reads and `createAnalystProfile`; `409` for
`createAnalystProfile`, the two transitions, and `defineWorkflow`; `412` and
`428` for the transitions and `setDefaultWorkflow`; `422` for
`registerAnalystImage`, `createAnalystProfile`, `defineWorkflow`, and
`setDefaultWorkflow`; `502` and `503` for `registerAnalystImage`; `404` for
`getDefaultWorkflow` (no default).

## Evidence

- `AnalystRegistryOpenApiTests` asserts, for every Registry operation, a JSON
  response schema for each success status and a JSON request schema for the
  four operations with a body.
- `OpenApiSnapshotTests` verifies the refreshed committed snapshot against the
  running API.
- The Registry integration tests assert that the response property names and
  nesting are unchanged.
