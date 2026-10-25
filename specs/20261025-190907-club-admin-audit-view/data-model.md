# Data Model: Club Admin Security Audit Evidence View

The feature adds no table and no column. It reads `socalytics.security_audit_event` and adds indexes.

## Security Audit Event (existing, read-only)

| Field | Stored as | Notes |
| --- | --- | --- |
| id | `uuid` | Tie-break of the order |
| occurred_at | `timestamptz` | Primary order key (newest first) |
| event_type, action | `text` | Dotted event type, for example `audit.evidence-read` |
| outcome | `text` | `succeeded`, `unchanged`, `denied`, `failed`, `refused` |
| actor_kind | `text` | `member`, `system`, `anonymous`, `analyst-manager`, `agent-workload` (the set the table accepts after migrations 0009 and 0024 and `PostgresAuditTrail` writes) |
| actor_account_id, session_id | `uuid` null | Absent for anonymous and system actors |
| resource_type, resource_id | `text`, `text` null | |
| team_id | `uuid` null | Absent for club-wide events |
| data_class | `text` | Default `DAT-001` |
| reason_code | `text` null | |
| details | `jsonb` | Stored allow-listed keys; only a subset is returned |
| correlation_id | `text` | |

Append-protected by the existing row and truncate triggers; this feature never writes to it except to append `audit.evidence-read` events through `IAuditTrail`.

## Audit Event View (response projection)

`id`, `occurredAt`, `eventType`, `action`, `outcome`, `actorKind`, `actorAccountId?`, `sessionId?`, `resourceType`, `resourceId?`, `teamId?`, `dataClass`, `reasonCode?`, `correlationId`, `details` (string map, allow-listed and value-checked per [research.md R7](research.md#r7-minimization-detail-allow-list)). No other stored field is returned.

## Audit Query

| Field | Rule |
| --- | --- |
| from, to | UTC instants; `from` inclusive, `to` exclusive; `from` must be before `to` when both are set |
| eventType | `^[a-z][a-z0-9.\-]{0,63}$` |
| outcome | one of the five stored outcomes |
| actorAccountId, teamId | GUID `D` format |
| resourceType | `^[a-z][a-z0-9\-]{0,63}$` |
| resourceId | 1 to 128 printable characters; requires `resourceType` |
| pageSize | integer 1 to `MaxPageSize`, default `DefaultPageSize` |
| continuationToken | at most 512 characters; bound to the other filters by fingerprint |

## Audit Page

`items` (Audit Event Views, newest first, at most `pageSize`) and `continuationToken?` (present only when more events exist). No count.

## Cursor

Opaque base64url of `occurred_at` ticks, `id`, and a 16-byte SHA-256 prefix of the canonical filter string (sorted `name=value` pairs of the filters, excluding `pageSize` and `continuationToken`). Mismatch, malformed length, or undecodable text is `invalid-continuation`. The cursor is unsigned, so a re-positioned cursor that still decodes and matches the filters is harmless: it only names a position in data the caller may read.

## Audit Read Event (appended)

| Field | Value |
| --- | --- |
| event_type / action | `audit.evidence-read` / `list` |
| resource_type / resource_id | `audit-events` / none |
| outcome and reason_code | `succeeded` (none); `denied` (`audit-read-not-permitted`); `refused` (`invalid-filter`, `invalid-continuation`, `page-size-out-of-range`, `invalid-time-range`, `rate-limited`) |
| actor, session, correlation | from the request context |
| details | `filtersSet` (sorted comma list of set filter names), plus `from`, `to`, `eventType`, `outcome`, `resourceType` when set and valid |

The keys `filtersSet`, `from`, `to`, `eventType`, `outcome`, and `resourceType` are added to the existing detail allow-list in `PostgresAuditTrail`.

## Indexes (migration `0033_identityaccess_audit_read_indexes.sql`)

`ix_audit_occurred_id (occurred_at DESC, id DESC)`; `ix_audit_type_occurred (event_type, occurred_at DESC, id DESC)`; partial `ix_audit_team_occurred` and `ix_audit_actor_occurred` on their non-null columns; existing `ix_audit_resource` serves resource filters.

## Options (`AuditEvidence`, development values)

`DefaultPageSize` 50, `MaxPageSize` 200, `ReadsPerMinute` 30.

## State transitions

None. Events are immutable; the view holds no state beyond in-memory page cursors in the Web UI.
