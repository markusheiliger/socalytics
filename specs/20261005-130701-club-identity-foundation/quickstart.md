# Quickstart: Club and Identity Foundation

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) |
**Contract**: [contracts/openapi.yaml](contracts/openapi.yaml) |
**Data model**: [data-model.md](data-model.md)

This guide validates the feature end to end after implementation. It covers
automated evidence first and then a manual walk-through against the local
Aspire composition. Successful local runs demonstrate behavior only. They do
not demonstrate deployment support or production readiness.

## Prerequisites

- The .NET SDK selected by `src/platform/global.json`.
- A running Docker engine, used by Testcontainers and by the AppHost
  PostgreSQL resource from the persistence foundation.
- The persistence foundation
  (`specs/20261005-130700-platform-persistence-foundation`) is merged. It
  provides the Migrator, the version triggers, and the Integration.Tests
  PostgreSQL fixture.
- For the manual walk-through only:
  - a trusted ASP.NET Core development certificate (`dotnet dev-certs https
    --trust`);
  - `curl` (on Windows, use `curl.exe` so the PowerShell alias is not used).

## 1. Build and run the automated evidence

From the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected outcome: every test passes. The feature's tests live in these places:

| Location | Evidence |
| --- | --- |
| `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Club/` | Hierarchy, season lifecycle, archived read-only, match immutability, edit versus lifecycle concurrency, and visibility filtering |
| `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/` | Bootstrap (including concurrent starts), sign-in, sessions, anti-forgery, lockout, credentials, membership and roles, last Club Admin, the authorization matrix, and audit evidence and secret scanning |
| `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/` | Layer and visibility rules for Identity and persistence types, anonymous-endpoint allow list, and anti-forgery coverage of unsafe methods |
| `src/platform/Tests/SocAlytics.Platform.Host.Tests/` | Aspire start with bootstrap parameters, `/health` readiness including `club-bootstrap`, and contract operations present in `/openapi/v1.json` |

To iterate on one area, narrow the run with the test runner's filter
option against the namespaces `SocAlytics.Platform.Integration.Tests.Club` or
`SocAlytics.Platform.Integration.Tests.IdentityAccess`. The full command above
remains the gate.

## 2. Automated scenario checklist

Each row names the test scenario that must exist and pass. The scenarios run
against disposable PostgreSQL containers with the production cookie flags.

| # | Scenario | Expected outcome | Spec |
| --- | --- | --- | --- |
| A1 | Fresh database with bootstrap configuration; start the API | One `club` row, one active member holding `club-admin`, one `club.bootstrapped` audit event without secrets, and readiness healthy | US1-1, FR-002 |
| A2 | Restart with the same configuration | No new account, member, club, or audit event | US1-2 |
| A3 | Start 4 API hosts concurrently on a fresh database | Exactly one club, one admin, and one bootstrap event; every host reports ready | US1-3, FR-002 |
| A4 | Restart with a different first-admin account name | Nothing changes; `club-bootstrap` readiness is unhealthy (`bootstrap-conflict`); a refusal audit event exists | FR-003 |
| A5 | Fresh database without bootstrap configuration | No club or account exists; readiness is unhealthy (`club-not-established`); no anonymous endpoint other than sign-in and redeem | Edge case, FR-003 |
| A6 | Sign in, then call `getCurrentMember` | `200` with identity and roles; `Set-Cookie` has `Secure`, `HttpOnly`, `SameSite=Strict`, and `Path=/`; the body contains no password, hash, or session token | US1-4, FR-023, FR-024 |
| A7 | Sign out, then replay the old cookie | `401` problem details with no redirect | US1-5, FR-027, FR-030 |
| A8 | Each authenticated unsafe-method operation without, and with a wrong, `X-CSRF-Token` | `403 antiforgery-failed` with no state change (verified by reading the database before and after) | US1-7, FR-025, SC-006 |
| A9 | Sign-in with an unknown account, wrong password, locked account, inactive member, and member without a password | Byte-identical `401 sign-in-failed` bodies apart from `correlationId` | US1-8, FR-018, SC-005 |
| A10 | Create a member, then redeem the set-password credential twice, after expiry, and for another account | First redemption `204`; the rest `400 credential-invalid`; the password is unchanged on failures | US2-1, US2-2, FR-016 |
| A11 | Assign `registrar`, then call the member's next request | The member holds Registrar; an audit event names the actor, target, role, and outcome | US2-3 |
| A12 | Deactivate a member with live sessions | The next request on every session gets `401`; roles are gone; sign-in fails generically | US2-4, FR-031 |
| A13 | Only one Club Admin: self-revoke, self-deactivate, or deactivation by another | `409 last-club-admin`, no change | US2-5, FR-034 |
| A14 | Two Club Admins revoke each other concurrently (repeated 50 times) | At most one succeeds and at least one Club Admin always remains | Edge case, FR-034, SC-008 |
| A15 | Deactivate and assign a role on the same member concurrently, in both orders | The member always ends deactivated with no roles, and no `412` or `428` occurs | US2-7, FR-049 |
| A16 | Registrar, Coach, and Viewer attempt each admin operation | `403 forbidden`, no change, and an `authorization.denied` audit event | US2-6, US3-5, FR-033 |
| A17 | Create a draft season, activate it, try activating a second one, then archive the first | Draft, then active; second activation `409 season-already-active`; archive succeeds; archiving a draft gets `409 invalid-state-transition` | US3-1 to US3-4 |
| A18 | Edit a team or match, or create a match, in an archived season | `409 season-archived`; reads still succeed | US3-4, US5-5, FR-008 |
| A19 | Archive and team edit concurrently | Either the edit commits before the archive, or it is rejected; no change lands after the archive | FR-008 |
| A20 | Create a team in a nonexistent season | `404`, no team created | US3-6, FR-012 |
| A21 | Coach on Team A, Viewer on Team B, nothing on Team C | Team A read and write allowed; Team B read only (`403` on write); Team C `404` with a denial audit event; `listTeams` returns A and B only | US4-1, US4-2, US4-4, FR-041 |
| A22 | Revoke the Team A role, then send the next request on the same session | `404` on Team A | US4-3, FR-039, SC-004 |
| A23 | Club Admin with no team roles reads every team and match | `200` | US4-5, FR-037 |
| A24 | Assign Viewer over an existing Coach role on the same team | One assignment with role `viewer` | US4-6, FR-035 |
| A25 | `updateMatch` with `opponent` or `teamId` in the body | `400 validation-failed` with field code `immutable` | US5-2, FR-011 |
| A26 | Two editors edit the same match with the same ETag | First `200`; second `412 version-mismatch`; the first change is preserved | US5-6, FR-042 |
| A27 | `updateClubSettings`, `updateTeam`, or `updateMatch` without `If-Match` or with `If-Match: *` | `428 version-required`, no change | FR-042, SC-008 |
| A28 | Lifecycle actions sent with a stale `If-Match` | The stale header is ignored, and the action is decided against the current state | FR-049, SC-008 |
| A29 | Reach the failed-attempt threshold, then use the correct password | Generic `401`; an `account.locked-out` audit event; Club Admin unlock lets sign-in succeed | US6-1, US6-2, FR-019 |
| A30 | Change the password with two live sessions | The current session keeps working; the other session gets `401` | US6-3, FR-020 |
| A31 | Redeem a reset credential with two live sessions | Both sessions get `401`; a second use gets `400 credential-invalid` | US6-4, US6-5, FR-021 |
| A32 | Idle and absolute expiry (using a fake time provider) | `401` after either limit | FR-026 |
| A33 | Admin ends a member's sessions | The member's next request on any session gets `401` | FR-028, SC-004 |
| A34 | Run every scenario, then scan all audit rows and captured logs for the passwords, credentials, session tokens, and anti-forgery tokens the test used | Zero matches; every event type in the [catalog](data-model.md#audit-event-catalog) appears at least once | FR-044 to FR-046, SC-007 |
| A35 | As the runtime role, `UPDATE`, `DELETE`, or `TRUNCATE` `security_audit_event` | Permission denied | FR-048 |
| A36 | Insert a second `club` row as the runtime role | Unique violation | FR-001 |
| A37 | Persistence structural test | Every versioned table has the advance trigger; both role tables have the child trigger; no `club_id` exists | FR-047, persistence FR-028 |
| A38 | Authorization matrix: every role × every operation × same, other, and unknown team, plus unauthenticated, revoked, and deactivated callers | 100% of disallowed combinations are denied and disclose no data | SC-003 |
| A39 | 100 sequential sign-in, sign-out, and single-resource reads and updates | 95th percentile under 1 second, reported as a local measurement | SC-009 |
| A40 | Each `operationId` in [contracts/openapi.yaml](contracts/openapi.yaml) | Present in `/openapi/v1.json` with the same path, method, and documented status codes | FR-043 |

## 3. Manual walk-through with the local composition

1. Start the composition:

   ```powershell
   dotnet run --project src/platform/SocAlytics.Platform.AppHost
   ```

   Expected outcome: the dashboard shows PostgreSQL, the Migrator (completed),
   and the API (healthy). The AppHost generates the
   `first-club-admin-password` secret parameter once per developer. Read it
   from the dashboard parameter view, or with:

   ```powershell
   dotnet user-secrets list --project src/platform/SocAlytics.Platform.AppHost
   ```

   The first admin's account name is the `first-club-admin-account-name`
   parameter (development default `club-admin`).

2. Copy the API's HTTPS URL from the dashboard into `$api`. Sign in with a
   cookie jar:

   ```powershell
   $api = "https://localhost:<port>"
   curl.exe -s -c jar.txt -H "Content-Type: application/json" `
     -d '{"accountName":"club-admin","password":"<generated password>"}' `
     "$api/api/v1/session"
   ```

   Expected outcome: `200` with `member.clubRoles` containing `club-admin`
   and an `antiforgeryToken`. Store the token in `$csrf`. The cookie jar holds
   `__Host-socalytics-session` and nothing else.

3. Check the anti-forgery protection:

   ```powershell
   curl.exe -s -b jar.txt -H "Content-Type: application/json" -d '{"name":"2026/27"}' "$api/api/v1/seasons"
   ```

   Expected outcome: `403` with `code` `antiforgery-failed`. Repeat the
   request with `-H "X-CSRF-Token: $csrf"`. Expected outcome: `201`, a season
   in state `draft`, and an `ETag` header.

4. Create a team in the season (`POST /api/v1/seasons/{seasonId}/teams`) and
   a member (`POST /api/v1/members` with `{"accountName":"coach.anna"}`).
   Expected outcome: the member response contains `setPasswordCredential`,
   and the response has `Cache-Control: no-store`.

5. Without the cookie jar, redeem the credential
   (`POST /api/v1/credentials/redeem` with the account name, the credential,
   and a new password of at least 12 characters). Expected outcome: `204`.
   Redeeming again gives `400` with `credential-invalid`.

6. As the admin, assign the Coach role
   (`PUT /api/v1/members/{memberId}/team-roles/{teamId}` with
   `{"role":"coach"}` and the CSRF header). Sign in as `coach.anna` with a
   second cookie jar.
   - Expected outcome: `GET /api/v1/teams` lists only that team.
   - Creating a match on it gives `201`.
   - `GET` on a team the coach holds no role on gives `404`.

7. Try a stale edit: send `PUT /api/v1/matches/{matchId}` twice with the same
   `If-Match`. Expected outcome: first `200`, then `412`. Without `If-Match`
   the result is `428`.

8. As the admin, revoke the coach's team role. Expected outcome: the coach's
   next request to the match gives `404` on the same session.

9. Activate and archive the season. Expected outcome: a further match edit
   gives `409` with `season-archived`, while reads still return `200`.

10. Stop the composition with `Ctrl+C`.

SC-001 (the first Club Admin signs in within 2 minutes of readiness) and SC-002
(season, team, member, and Coach role in under 5 minutes) are confirmed by
steps 1 to 6 and by the timed Host and Integration tests.
