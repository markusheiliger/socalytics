# Data Model: Member Invitations by Email

Persistence follows the platform conventions in [src/platform/README.md](../../src/platform/README.md): forward-only DbUp
migrations, one migration file numbered at implementation time as the next free number
(`NNNN_identityaccess_member_invitations.sql`), `socalytics.attach_version_trigger` for versioned tables,
`socalytics.attach_aggregate_child_triggers` for child tables, and a `PersistedTableClassifications` entry for every
new table without `version`.

## MemberInvitation (new table `socalytics.member_invitations`, versioned)

A pending offer of membership to one address; the aggregate root of the invitation. Its version changes only through
Club Admin actions and acceptance, never through message delivery.

| Field | Type | Rules |
| --- | --- | --- |
| `id` | uuid | Primary key. |
| `address` | text | The invited address as entered (same length and trim rules as `member_email_addresses.address`); readable by Club Admins only. |
| `address_normalized` | text | Normalized address (same normalization as member addresses). |
| `address_hash` | text | Hash of the normalized address, as `email_messages.address_hash`. |
| `account_name` | text | The account name the member will get (`AccountName` rules: 3 to 64 characters). |
| `account_name_normalized` | text | `AccountName.Normalized`. |
| `state` | text | `pending`, `accepted`, `revoked`. |
| `invited_by` | uuid | References `member_account (id)`; the inviting Club Admin. |
| `created_at` | timestamptz | Creation time. |
| `resend_count` | integer | Number of resends (0 on creation). |
| `revoked_by` | uuid NULL | References `member_account (id)`; set exactly when `state = 'revoked'`. |
| `revoked_at` | timestamptz NULL | Set exactly when `state = 'revoked'`. |
| `accepted_member_id` | uuid NULL | References `member_account (id)`; set exactly when `state = 'accepted'`. |
| `accepted_at` | timestamptz NULL | Set exactly when `state = 'accepted'`. |
| `version` | bigint | Optimistic concurrency (`If-Match`). |

Constraints and indexes: unique partial indexes on `account_name_normalized` and on `address_normalized` where
`state = 'pending'`; index on `(created_at DESC)` for the list.

## MemberInvitationTeamRole (new table `socalytics.member_invitation_team_roles`, aggregate child)

| Field | Type | Rules |
| --- | --- | --- |
| `invitation_id` | uuid | References `member_invitations (id)`; part of the primary key. |
| `team_id` | uuid | References `team (id)`; part of the primary key (one role per team). |
| `role` | text | `coach` or `viewer` (team role wire values). |

Attached with `socalytics.attach_aggregate_child_triggers`; classified as
`ChildOf("member_invitations", "invitation_id")`. Roles are written only at creation.

## MemberInvitationLink (new table `socalytics.member_invitation_links`, unversioned)

One link per invitation message; creation and every resend create a new link and supersede the open ones, so only the
newest link can work. Modelled on `email_confirmations`.

| Field | Type | Rules |
| --- | --- | --- |
| `id` | uuid | Primary key. |
| `invitation_id` | uuid | References `member_invitations (id)`. |
| `state` | text | `pending`, `issued`, `used`, `superseded`. |
| `token_hash` | bytea NULL | SHA-256 of the token, written by the delivery worker; unique when present; never the raw token. |
| `created_at` | timestamptz | When the creation or resend created the link (used for the resend limit). |
| `issued_at` | timestamptz NULL | When the worker issued the current token. |
| `expires_at` | timestamptz NULL | `issued_at` plus `Invitations:LinkLifetime`. |
| `used_at` | timestamptz NULL | When the acceptance used it. |

Constraints mirror `email_confirmations`: `pending` has no token hash; `issued` and `used` carry token hash,
`issued_at`, and `expires_at`; `superseded` carries them only when issued before; `used` exactly when `used_at` is set;
unique index on `token_hash` where present; index on `invitation_id` for `state IN ('pending', 'issued')`.
Classification: unversioned ("single-use invitation link state; the state transition is the guard").

### State transitions

```text
Invitation:  pending --(Club Admin revokes)--> revoked
             pending --(invitee accepts through the open, unexpired link)--> accepted
Link:        pending --(worker sends, issues token)--> issued --(worker retries, new token)--> issued
             pending|issued --(resend or revoke)--> superseded
             issued --(acceptance)--> used
```

`accepted`, `revoked`, `used`, and `superseded` are final. A revoked or accepted invitation has no open link.

### Derived display state

| Invitation state | Open link | Display state |
| --- | --- | --- |
| `pending` | `pending`, or `issued` with `expires_at` > now | `pending` |
| `pending` | `issued` with `expires_at` <= now | `expired` |
| `accepted` | - | `accepted` |
| `revoked` | - | `revoked` |

An expired invitation keeps its reservations until it is resent (pending again) or revoked.

## Email message (existing table `socalytics.email_messages`)

- `account_id` becomes nullable; new nullable column
  `invitation_link_id uuid REFERENCES socalytics.member_invitation_links (id)`.
- New check: `(notification_type = 'member-invitation') = (invitation_link_id IS NOT NULL AND account_id IS NULL)`, so
  every other type keeps a recipient member.
- `notification_type` gains the value `member-invitation` (a code in `NotificationTypes`, audience `Invitee`).
- The list's delivery state is the state of the message of the invitation's newest link (`queued` and `sending` show as
  queued, `sent`, `failed`; `discarded` shows as failed).

## Member account, address, and team roles (existing)

- Acceptance creates a `member_account` row (active, password set, no password-change requirement) through the
  existing credential service, a `member_email_addresses` row in state `verified` with `verified_at` = acceptance time
  and `set_by` = the inviting Club Admin, and `team_role_assignment` rows for every invited team.
- `CreateMemberHandler` additionally refuses an account name reserved by a pending invitation.

## Security audit events (existing `security_audit_event`)

| Event type | Outcome | Resource | Details | Reason codes |
| --- | --- | --- | --- | --- |
| `invitation.created` | succeeded | invitation | `maskedAddress`, `teamCount` | - |
| `invitation.created` | refused | none | `maskedAddress` | `email-address-in-use`, `account-name-taken` |
| `invitation.issued` | succeeded | invitation | `maskedAddress` | - |
| `invitation.resent` | succeeded or refused | invitation | `maskedAddress` | `rate-limited`, `invitation-not-open`, `version-mismatch` |
| `invitation.revoked` | succeeded or refused | invitation | `maskedAddress` | `invitation-not-open`, `version-mismatch` |
| `invitation.accepted` | succeeded | invitation | `memberId`, `maskedAddress` | - |
| `invitation.acceptance-refused` | failed | none | - | `invitation-invalid`, `password-policy` |

Acceptance also records the existing `member.created` and `team-role.assigned` events. No event, log, or metric
carries a token, password, full address, or message body.

## Configuration (`Invitations` section)

| Key | Development and test value | Production |
| --- | --- | --- |
| `LinkLifetime` | 7 days | Open / Blocking |
| `MaxResendsPerHour` | 3 per invitation | Open / Blocking |
| `TokenAttemptsPerMinute` | 10 per client (checks and acceptances) | Open / Blocking |
