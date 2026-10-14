# Contract: Routes and Roles (additions)

Extends the foundation's
[routes-and-roles.md](../../archive/20261012-150657-web-ui-foundation/contracts/routes-and-roles.md).
"Editor" means Club Admin, or Coach of the match's team; "reader" means an
editor or a Viewer of that team.

| Route | View | Allowed for | Platform operations |
| --- | --- | --- | --- |
| `/teams/:teamId` | Team detail, now with the team's match list (newest kickoff first, paged) and "New match" for editors | Reader of the team (Club Admin sees all teams) | foundation operations, `GET /api/v1/teams/{teamId}/matches` |
| `/teams/:teamId/matches/new` | Create match | Editor; hidden when the team's season is archived | `POST /api/v1/teams/{teamId}/matches` |
| `/matches/:matchId` | Match view: details; recordings (accepted versions, pending uploads of this member in this browser, add recordings for editors); finalized sets with their analysis run states; finalize action for editors | Reader | `GET /api/v1/matches/{matchId}`, `GET …/recording-lineage`, `GET …/analysis-runs`, `GET …/upload-sessions/{id}`, upload operations for editors |
| `/matches/:matchId/edit` | Edit kickoff time, venue (home, away, or neutral), competition (opponent read-only) | Editor | `GET`/`PUT /api/v1/matches/{matchId}` |
| `/matches/:matchId/finalize` | Select, order, choose mappings, confirm | Editor | `POST /api/v1/matches/{matchId}/recording-set-versions` |
| `/matches/:matchId/recordings/:recordingVersionId` | Recording version: size, digest, upload time, mappings; "Revise mapping" for editors | Reader | `GET …/recording-versions/{id}`, `POST …/recording-versions/{id}/timeline-mappings` |

## Shell additions

- An upload indicator in the application bar shows the number of active and
  queued uploads and the overall progress, and opens a panel that lists each
  upload with its match and state. It is visible on every route while uploads
  exist for the signed-in member.
- `beforeunload` asks for confirmation while any upload is `transferring`,
  `starting`, or `completing` (FR-012).
- Sign-out asks for confirmation while an upload is transferring, then purges
  the member's journal before ending the session (FR-014).

## Rules

- Guards decide from the session's club and team roles. The match routes load
  the match first and then apply the team role; a platform `403` shows the
  not-permitted view, as in the foundation.
- Editors' actions are hidden for readers and while the team's `seasonState` is
  `archived`; any platform refusal (`season-archived`, `403`) is still shown.
- No route offers playback, preview, or download of recordings (FR-024), and no
  action edits, reorders, or deletes accepted lineage (FR-021).
