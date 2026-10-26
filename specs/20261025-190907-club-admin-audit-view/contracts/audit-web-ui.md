# Contract: Audit Web UI Area

## Route and role

| Route | Element | Access |
| --- | --- | --- |
| `/audit` | Audit list with filters, paging, refresh, and detail dialog | Club Admin only (`clubAdminOnly`); every other member sees the not-permitted view and no audit request is sent |

The navigation entry "Audit" appears only for Club Admins, next to the other Club Admin entries. The detail dialog is part of the list view because the platform offers no get-by-id operation.

## Address filters

| Search parameter | Filter | Dropped when |
| --- | --- | --- |
| `from`, `to` | time range | not a valid instant |
| `eventType` | event type | not matching the platform pattern |
| `outcome` | outcome | not one of the five outcomes |
| `actor` | actor account | not a GUID |
| `resourceType`, `resourceId` | resource | pattern mismatch; `resourceId` without `resourceType` |
| `team` | team | not a GUID |

Unknown parameters and invalid values are ignored and never sent. The cursor and page number are never part of the address.

## Adapter (`src/platform/adapters/auditAdapter.ts`)

`listAuditEvents(filters, continuationToken?, signal?)` returns `{ items: AuditEvent[]; continuationToken?: string }` mapped from the generated `AuditEventPageDto`; failures become `PlatformFailure` with the platform's reason (`not-permitted`, `invalid-filter` with the field, `invalid-continuation`, `rate-limited` with retry-after, `unavailable`). Components never import the generated client.

## States and behavior

- Loading, empty, error with retry, not-permitted, invalid-filter (shown next to the filter), invalid-continuation (restart at the first page with an explanation), rate-limited (retry after the stated time), and connection-required states are explicit.
- A persistent notice states that the view is read-only development evidence and that reading it is audited.
- Next and previous keep the platform's page boundaries with an in-memory cursor stack; refresh re-reads the first page for the current filters and keeps the old page visible until the new one arrives; nothing refreshes automatically.
- The detail dialog shows every returned field, absent values as an explicit "absent" marker, copyable identifiers, and platform text as plain text with bounded wrapping.
- No audit data is written to browser storage, IndexedDB, or service-worker caches; reads are not queued offline.
- There is no export, redaction, deletion, retention, integrity, hold, alerting, or auto-refresh control.
