# Contract: Member Invitations API and Web UI Routes

The operations are part of the platform's OpenAPI document (`/openapi/v1.json`) and its committed snapshot, which the
Web UI's Kiota client is generated from. All use the existing problem-details shape (`ProblemResults`) and accept only
`application/json` bodies.

## Club Admin operations

Mapped with `MapMemberApi()` (policy `ActiveMember`, `SessionAntiforgeryFilter`) and authorized with
`ClubPermission.Administer`; any other member gets `403` and learns nothing about invitations.

### `POST /api/v1/invitations` (`createInvitation`)

Request body:

```json
{
  "address": "person@example.org",
  "accountName": "jdoe",
  "teamRoles": [{ "teamId": "<uuid>", "role": "coach" }]
}
```

| Status | When | Body |
| --- | --- | --- |
| `201 Created` | Invitation created and its message queued | `Invitation`, `ETag` with the version, `Location` |
| `400 Bad Request` | Invalid address, account name, team, role, or duplicate team | problem, field violations (`address`, `accountName`, `teamRoles[i].teamId`, `teamRoles[i].role`) |
| `409 Conflict` | Address is the current address of a member or of a pending or expired invitation | problem `email-address-in-use` |
| `409 Conflict` | Account name belongs to a member or a pending or expired invitation | problem `account-name-taken` |

### `GET /api/v1/invitations` (`listInvitations`)

Query: optional `state` (`pending`, `expired`, `accepted`, `revoked`). Answer `200` with `Invitation[]`, newest first.

### `POST /api/v1/invitations/{invitationId}/resend` (`resendInvitation`)

Requires `If-Match` with the invitation version (`IfMatchHeader`).

| Status | When | Body |
| --- | --- | --- |
| `200 OK` | New message queued; previous link invalid | `Invitation`, new `ETag` |
| `404 Not Found` | Unknown invitation | problem |
| `409 Conflict` | Invitation accepted or revoked | problem `invitation-not-open` |
| `412 Precondition Failed` / `428 Precondition Required` | Stale or missing version | problem (existing conditional-request codes) |
| `429 Too Many Requests` | Resend limit per invitation reached, with `Retry-After` | problem `invitation-resend-rate-limited` |

### `POST /api/v1/invitations/{invitationId}/revoke` (`revokeInvitation`)

Requires `If-Match`. Answers `200` with the revoked `Invitation`, or `404`, `409 invitation-not-open`, `412`, `428` as
for resend.

### `Invitation`

```json
{
  "id": "<uuid>",
  "state": "pending | expired | accepted | revoked",
  "address": "person@example.org",
  "accountName": "jdoe",
  "teamRoles": [{ "teamId": "<uuid>", "teamName": "U17", "role": "coach" }],
  "invitedBy": { "id": "<uuid>", "accountName": "admin" },
  "createdAt": "2026-10-17T20:00:00Z",
  "linkIssuedAt": "2026-10-17T20:00:05Z",
  "linkExpiresAt": "2026-10-24T20:00:05Z",
  "deliveryState": "queued | sent | failed",
  "resendCount": 0,
  "revokedAt": null,
  "acceptedAt": null,
  "acceptedMemberId": null,
  "version": 1
}
```

## Anonymous operations

JSON-only, `Cache-Control: no-store`, rate limited per client with the policy `invitation-token`
(`Invitations:TokenAttemptsPerMinute`); beyond it `429` problem `invitation-rate-limited` with `Retry-After`.

### `POST /api/v1/invitations/checks` (`checkInvitation`)

Request `{ "token": "<token from the link fragment>" }`.

| Status | When | Body |
| --- | --- | --- |
| `200 OK` | Pending invitation whose current link has not expired | `{ "clubName": "FC Example", "accountName": "jdoe" }` |
| `400 Bad Request` | Token missing, unknown, expired, superseded by a resend, revoked, or accepted | problem `invitation-invalid` (one neutral code) |

The check never changes the invitation.

### `POST /api/v1/invitations/acceptances` (`acceptInvitation`)

Request `{ "token": "<token>", "password": "<password>" }`.

| Status | When | Body |
| --- | --- | --- |
| `204 No Content` | Member created and active, address verified, team roles assigned, invitation accepted | empty |
| `400 Bad Request` | `password` missing or violates the password policy (link stays usable) | problem, field violation `password` (`required` or `password-policy`) |
| `400 Bad Request` | Token invalid in any way | problem `invitation-invalid` |

An acceptance never establishes a session.

## Invitation message

- Type `member-invitation`, an account message of the audience `Invitee`; not listed by
  `getOwnNotificationPreferences` and refused by `setOwnNotificationPreference` as an unknown type.
- Link: `<Web UI base address>/invitations/accept#token=<token>`.
- Text: invitation to join the club's SocAlytics (club name), ignore note, link lifetime, link; no account name,
  password, inviting member, roles, or club data beyond the club name.

## Web UI routes

| Route | Access | Purpose |
| --- | --- | --- |
| `/members` | Club Admins | Adds "Invite member" and the invitations section with state filter, resend, and confirmed revoke. |
| `/invitations/accept` | public | Reads `token` from the fragment, removes it from the address bar, checks it, shows club and account name and the password form, accepts, and leads to `/sign-in`; shows one neutral message for `invitation-invalid`. |
