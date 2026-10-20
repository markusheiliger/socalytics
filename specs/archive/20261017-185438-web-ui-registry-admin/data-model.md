# Data Model: Web UI Analyst Registry Administration

This feature adds no persisted data. The platform's Registry records are
defined by [Analyst Registry](../20261012-152148-analyst-registry/data-model.md);
this document defines the Web UI's application-owned models in
`src/clients/web/src/platform/models/registry.ts`, which the Registry adapter
maps from the generated client (see [contracts/registry-adapter.md](contracts/registry-adapter.md)).
Every enumerated value is a `string` union of the known values plus `string`,
so unknown values are kept and shown raw (FR-022).

## Digest

A display helper, not a stored type: the full value (for example
`sha256:<64 hex>` or `sha-256:<64 hex>`) and a shortened form (algorithm and
the first 12 hexadecimal characters). Both are always available (FR-010).

## ImageSummary (`listAnalystImages` item)

| Field | Type | Notes |
| --- | --- | --- |
| `imageDigest` | string | Identity |
| `capabilityId` | string | |
| `capabilityVersion` | string | |
| `registeredAt` | Date | |

## CapabilityDeclaration (`getAnalystImage`, `registerAnalystImage`)

| Field | Type | Source |
| --- | --- | --- |
| `imageDigest`, `repository`, `manifestDigest` | string | DTO |
| `capability` | `{ id, version, tier, executionScope }` | DTO; `tier` `low-level` \| `high-level`, `executionScope` `segment` \| `match` |
| `modelDigest` | string | DTO |
| `source` | string | DTO (`seed` for the development seed) |
| `registeredAt` | Date | DTO |
| `registeredBy` | MemberRef | DTO id, resolved per research.md § R6 |
| `upstream` | `{ capabilityId, capabilityVersion, schemaVersion, kind }[]` | manifest; `kind` `required` \| `optional` \| `conditional` |
| `videoAccess` | string | manifest; `required` \| `optional` \| `forbidden` |
| `outputSchemaVersion` | string | manifest `output.schemaVersion` |
| `compatibleProfiles` | string[] | manifest |
| `runtime` | `{ constraints: string[], accelerators: string[] }` | manifest |
| `resources` | `Record<string, string>` | manifest |
| `requirements` | `{ minMaxAttempts, minLeaseSeconds, minStaleTimeoutSeconds, minExecutionTimeoutSeconds }` | manifest |
| `determinism` | `{ deterministic, idempotent }` | manifest |
| `manifestText` | string | the stored manifest, pretty-printed, shown as text only |

Manifest fields that are missing or of an unexpected type are `undefined` and
shown as "not declared"; the declaration still loads.

## ProfileVersion (`listAnalystProfiles` item, `getAnalystProfile`, transitions)

| Field | Type | Notes |
| --- | --- | --- |
| `profileId`, `version` | string, number | Identity |
| `rowVersion` | number \| undefined | From the `ETag` of a single-version read or transition; required for `If-Match` |
| `profileDigest`, `imageDigest` | string | |
| `state` | `draft` \| `approved` \| `retired` \| string | |
| `content` | ProfileContent | |
| `createdAt`, `createdBy` | Date, MemberRef | |
| `approvedAt`, `approvedBy` | Date?, MemberRef? | |
| `retiredAt`, `retiredBy` | Date?, MemberRef? | |

**ProfileContent**: `capabilityId`, `capabilityVersion`, `modelDigest`,
`runtimeConstraints: string[]`, `accelerators: string[]`,
`resources: Record<string, string>`, `maxAttempts`, `leaseSeconds`,
`staleTimeoutSeconds`, `executionTimeoutSeconds` (numbers), `videoPolicy`,
`inputSchemaVersions: Record<string, string>`, `outputSchemaVersion`.

**State transitions** (decided by the platform): `draft → approved`
(approve), `draft → retired` and `approved → retired` (retire). The Web UI
offers *Approve* only for `draft` and *Retire* only for `draft` and
`approved`; the platform's refusal is still handled (FR-014, FR-017).

## WorkflowSummary (`listWorkflows` item)

`workflowId`, `workflowVersion`, `definitionDigest`, `createdAt`, and the
derived `isDefault` (equal to the loaded default's id and version).

## WorkflowVersion (`getWorkflow`, `defineWorkflow`)

| Field | Type | Notes |
| --- | --- | --- |
| `workflowId`, `workflowVersion` | string | Identity; the version is an opaque string |
| `definitionDigest` | string | |
| `requestedCapabilities` | string[] | |
| `completionPolicy` | `all-requested-capabilities` \| string | |
| `segmentationPolicy` | `{ version: number, durationSeconds: number, digest: string }` | |
| `nodes` | `{ nodeKey, profileId, profileVersion, profileDigest }[]` | sorted by node key |
| `edges` | WorkflowEdge[] | in definition order |
| `createdAt`, `createdBy` | Date, MemberRef | |

**WorkflowEdge**: `upstreamNodeKey`, `dependentNodeKey`,
`kind` (`required` \| `optional` \| `conditional` \| string), and for
conditional edges `predicate` `{ kind: 'capabilityRequested' | 'nodeAccepted' | string, capabilityId?, nodeKey? }`.
The edge description in words is derived from these fields only.

## DefaultWorkflow (`getDefaultWorkflow`, `setDefaultWorkflow`)

`workflowId`, `workflowVersion`, `changedAt`, `changedBy` (MemberRef), and
`rowVersion` from the `ETag`. `null` when the platform answers `404` (no
default set).

## Drafts (form state, in memory only)

- **ImageRegistrationDraft**: `reference` (string). Client check: contains
  `@sha256:` and 64 hexadecimal characters.
- **ProfileDraft**: `imageDigest` (fixed), `profileId` (existing id of the
  same capability or a new id), `content` (ProfileContent, prefilled from the
  declaration per research.md § R7).
- **WorkflowDraft**: `workflowId`, `workflowVersion`, `requestedCapabilities`
  (default all node capabilities), `completionPolicy` (preselected),
  `segmentationPolicy`, `nodes` (node key and approved profile version;
  flagged when prefilled from a version whose profile is no longer approved),
  `edges`. Client checks: unique node keys, edges and predicates refer to
  nodes of the draft, at least one node, no flagged node.

## Outcomes

- **RegistryRefusal**: `{ operation, code, details: { field, code }[], retryable }`,
  produced by the adapter per research.md § R4; `code` is shown through a
  translation key per known code and raw otherwise.
- **Changed**: the record changed since it was loaded (`412`, or `428` for the
  default); the view reloads the record and asks for a new decision.
- **MemberRef**: `{ id, displayName?, kind: 'member' | 'seed' | 'unknown' }`.
