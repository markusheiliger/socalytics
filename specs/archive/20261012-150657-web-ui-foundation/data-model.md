# Data Model: Web UI Foundation

The Web UI holds no authoritative data. These are its in-memory models.

## Session (client view)

Source: `SessionInfoDto` from `POST /api/v1/session` and `GET /api/v1/session`.

| Field | Use |
| --- | --- |
| `sessionId`, `idleExpiresAt`, `absoluteExpiresAt` | Display and proactive refresh of the session query |
| `antiforgeryToken` | Kept in memory only; sent as `X-CSRF-Token` on state-changing requests |
| `passwordChangeRequired` | Routes to the password-change view only |
| `member.id`, `member.accountName`, `member.membershipStatus` | Shell and account view |
| `member.clubRoles[]` (`ClubAdmin`, `Registrar`) | Navigation and guards |
| `member.teamRoles[]` (`teamId`, `teamName`, `seasonId`, `role` = `Coach` or `Viewer`) | Team navigation |

States: `unknown` (start, restoring) → `signedOut` | `passwordChangeRequired` |
`signedIn`; any refusal for an ended session → `signedOut` with reason
`sessionEnded`.

## Cached platform entities

TanStack Query caches the club, seasons, teams, members, and role assignments.
Each record keeps its `version` for `If-Match`. On sign-out or session end, the
whole query cache is cleared.

## Connectivity state

`reachable` | `unreachable` (with last failure time) | `checking`.

## Set-password form

Fields: sign-in name, credential code, new password, confirmation. The code is
held in the form only and cleared after submission; it is never part of a URL.

## Routes

See [contracts/routes-and-roles.md](contracts/routes-and-roles.md).
