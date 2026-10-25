# Contract: Placement, Roles, and States

No new route. The segments area is a section of the existing match page `/matches/:matchId`, below the recordings and
analysis run list and above the triage section.

## Roles

| Member | Area shown | Requests sent |
| --- | --- | --- |
| Club Admin of the owning club | Yes | list on open and refresh; purge on confirmation |
| Coach, Analyst, Viewer, Club Admin of another club | No | None |

The platform is authoritative: a `403` shows the not-permitted state inside the area without segment data and without
the purge action.

## States

| State | Trigger | Shown |
| --- | --- | --- |
| Loading | list pending, no data | status text |
| Empty | list returned no segments | text that none exist and are created on demand; no purge |
| List | segments returned | summary, table in platform order, refresh, purge when `purgeable > 0`, else a reason |
| Refreshing | refetch with data | previous list stays, status text, controls disabled |
| Not permitted | `403` | explanation, no data, no purge |
| Session ended | `401` | existing sign-in flow; no stale data |
| Error | network or `5xx` | explanation with retry; never an empty list |
| Offline | connectivity unreachable | connection-required text; purge and refresh disabled |

## Purge confirmation (dialog)

Title and body state: the match (opponent name), that segments not in use are deleted and become absent, that segments
in use are kept and reported, and that a purged segment is rematerialized on demand. Actions: Cancel (sends nothing) and
Purge. While pending: progress status, both actions and refresh disabled, dismissal blocked. Outcome: `purged` and
`inUse` counts as returned (zero purged is a normal outcome). Failure (`403`, `404`, `503`, network): plain-language
cause, a note that segments already purged by the call stay purged, the list re-read, and a retry action. Focus is
trapped in the dialog and returns to the purge button on close; outcomes and errors are announced (`role="status"` or
`role="alert"`).

## Accessibility and i18n

Semantic table with caption and headers, states and in-use markers as text, accessible names on every control, texts
only from the `segments` namespace (and shared `shell` texts), dates, times, and sizes formatted for the user's locale.
