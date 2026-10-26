# Contract: Security Audit Events Operation

The operation is part of the platform's member API and is described by the generated OpenAPI document (`/openapi/v1.json`) and its committed snapshot used by the Web UI; this file states the shape the implementation MUST publish with typed schemas (`AuditEvidenceOpenApiTests`).

## Operation

`GET /api/v1/audit/events`: operation id `listSecurityAuditEvents`, tag `Security audit`, member session required (`ActiveMember`), Club Admin only, rate-limit policy `audit-evidence-read`.

### Query parameters (all optional)

| Name | Type | Rule |
| --- | --- | --- |
| `from` | date-time | Inclusive start |
| `to` | date-time | Exclusive end; must be after `from` |
| `eventType` | string | `^[a-z][a-z0-9.\-]{0,63}$` |
| `outcome` | enum | `succeeded`, `unchanged`, `denied`, `failed`, `refused` |
| `actorAccountId` | uuid | |
| `resourceType` | string | `^[a-z][a-z0-9\-]{0,63}$` |
| `resourceId` | string | 1 to 128 characters; requires `resourceType` |
| `teamId` | uuid | |
| `pageSize` | integer | 1 to 200, default 50 (development values) |
| `continuationToken` | string | Opaque; at most 512 characters; only valid with the filters it was issued for |

### Responses

| Status | Body | When |
| --- | --- | --- |
| 200 | `AuditEventPageDto` | Valid request by a Club Admin; `Cache-Control: no-store` |
| 400 | validation problem with field violations | Invalid filter, page size, time range, or continuation token; violation reasons `invalid`, `out-of-range`, `invalid-time-range`, `required-with-resource-type`, `invalid-continuation` |
| 401 | problem | No member session |
| 403 | problem | Authenticated member that is not a Club Admin; identical whatever the evidence holds |
| 429 | problem `audit-read-rate-limited` with `Retry-After` | Per-member limit exceeded |
| 500 | problem | The read could not be audited or failed; no events returned |

### Schemas

- `AuditEventPageDto`: `items` (array of `AuditEventDto`), `continuationToken` (string, nullable; absent when no further events exist). No count.
- `AuditEventDto`: `id` (uuid), `occurredAt` (date-time), `eventType`, `action`, `outcome` (enum), `actorKind`, `actorAccountId?`, `sessionId?`, `resourceType`, `resourceId?`, `teamId?`, `dataClass`, `reasonCode?`, `correlationId`, `details` (object with string values).

### Behavior guarantees

1. Order is `(occurredAt, id)` descending and independent of filters.
2. Filters combine with AND; an unknown but well-formed account, team, or resource returns an empty page.
3. A continuation token used with different filters, undecodable, or malformed is refused with `invalid-continuation`.
4. Only allow-listed detail keys and value-checked values are returned ([research.md R7](../research.md#r7-minimization-detail-allow-list)).
5. Every authenticated request appends one `audit.evidence-read` event (see [data-model.md](../data-model.md#audit-read-event-appended)) holding the filter shape only; a successful read is appended in the reading transaction, so failure to record means no events are returned.
6. The response is never cached and nothing about it is logged beyond ordinary request identifiers and reason codes.

## Machine-readable contracts

No file is added to the repository-root `contracts/` folder: the operation has no Manager, Analyst, or workload consumer, and the member API contract is the OpenAPI document.
