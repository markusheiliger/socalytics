# Research: Member Invitations by Email

All Technical Context items are resolved from the architecture, the existing code, the clarifications in
[spec.md](spec.md), and the mechanics that [Self-Service Password Recovery](../20261017-200823-password-recovery/research.md)
defines for the same email channel. No new framework, SDK, or tool is needed.

## R1 - Invitation as a separate record

- **Decision**: A new aggregate `MemberInvitation` (Domain `IdentityAccess`) stored in a new versioned table
  `member_invitations`; no member account exists until acceptance. The invitation stores the invited address (full,
  normalized, and hashed), the reserved account name (and its normalized form), the inviting Club Admin, and its team
  roles in a child table `member_invitation_team_roles`. States: `pending`, `accepted`, `revoked`; `expired` is derived
  when a pending invitation's open link has passed its expiry (and is shown and enforced as such), so no background
  job is needed. Links live in a separate unversioned table (R3), so message delivery never changes the invitation's
  version and a Club Admin's conditional resend or revoke is not refused because a message was sent meanwhile.
- **Rationale**: `member_account.membership_status` allows only `active` and `deactivated`, and every role assignment,
  credential, address, sign-in, and session path requires an active member (`AssignTeamRoleHandler`,
  `IssueCredentialHandler`, `AccessAuthorizer`, `EmailDeliveryProcessor`). A separate record satisfies FR-004 and FR-012
  without a new membership status or changes to those paths, and revoked or expired invitations release their account
  name and address without deleting accounts.
- **Alternatives considered**: A new membership status `invited` on `member_account` (rejected: touches every
  membership check, makes revoked invitees permanent accounts that hold their names, and lets reactivation bypass
  acceptance); reusing `one_time_credential` with a new purpose (rejected: credentials target an existing account).

## R2 - Reservations and conflicts

- **Decision**: Partial unique indexes on `member_invitations (account_name_normalized)` and `(address_normalized)`
  where `state = 'pending'`. Creation checks, in one unit of work, `member_account.normalized_account_name` and
  `member_email_addresses.address_normalized` (any state) and refuses with `account-name-taken` or
  `email-address-in-use` (unique violations map to the same codes). `CreateMemberHandler` also refuses a name reserved
  by a pending invitation with `account-name-taken`. An expired invitation keeps its reservation until it is revoked
  or resent, so resending an expired invitation never fails for a conflict.
- **Rationale**: FR-003 and the edge case "Account name taken at acceptance" (acceptance never fails for a name
  conflict). Members may still share addresses (setting a member address is unchanged).
- **Alternatives considered**: Releasing reservations at expiry (rejected: resend of an expired invitation could then
  conflict); checking only at acceptance (rejected by FR-003).

## R3 - Link token and delivery

- **Decision**: A new unversioned table `member_invitation_links`, modelled on `email_confirmations`: creation and
  every resend create a `pending` link and mark the invitation's open links `superseded`. The invitation message is an
  `email_messages` row of the new type `member-invitation` with a new nullable column `invitation_link_id`;
  `account_id` becomes nullable with a check that exactly the invitation type has no account and a link.
  `EmailDeliveryProcessor` handles the type like confirmations: it re-checks that the link is still `pending` or
  `issued`, that its invitation is still `pending`, and that the address hash matches, then generates a token with
  `ConfirmationTokens`, stores its hash and expiry on the link (`Invitations:LinkLifetime`), and sends to the
  invitation's address. The link is
  `<Web UI base address>/invitations/accept#token=<token>`, built like the recovery link of
  [Self-Service Password Recovery](../20261017-200823-password-recovery/contracts/password-recovery-api.md).
- **Rationale**: FR-005, FR-006, and FR-008: same worker-issued, hash-only, fragment-carried token as verification
  and recovery; a resend supersedes the earlier link, so an earlier unsent message is discarded and an earlier
  sent link fails. Keeping links out of the versioned invitation keeps delivery from changing its version.
- **Alternatives considered**: Storing the token hash on the invitation (rejected: every delivery would bump the
  version and refuse Club Admins' conditional actions); a separate invitation outbox (rejected: duplicates delivery
  rules); issuing the token
  synchronously in the request (rejected: the raw token would pass through the API process and its response).

## R4 - Acceptance

- **Decision**: `AcceptInvitationHandler` validates the password policy first (a violation leaves the link usable),
  then in one unit of work locks the link by token hash and its invitation, requires the link `issued` and unexpired
  and the invitation `pending`, creates the account through
  `IAccountCredentialService.CreateAccountAsync` with the reserved name and the chosen password (no password-change
  requirement), writes the member's address as `verified` (`set_by` = inviting Club Admin), upserts the invited team
  roles (`IMemberAccountStore.UpsertTeamRoleAsync`; teams are never deleted), marks the invitation `accepted` with the
  member and time, marks the link `used`, and records `member.created`, `team-role.assigned`, and `invitation.accepted` audit
  events. No session is created; the Web UI leads to `/sign-in`.
- **Rationale**: FR-009 to FR-012; reuses the account creation and role assignment code of member creation and the
  completion pattern of recovery.
- **Alternatives considered**: Signing the invitee in (rejected: FR-011 and consistency with recovery); accepting with
  an external provider (rejected by the clarification).

## R5 - Administration and concurrency

- **Decision**: Club Admin operations under `MapMemberApi()` with `ClubPermission.Administer`: create, list (newest
  first, optional `state` filter, derived `expired`), resend, and revoke. Resend and revoke require `If-Match` with the
  invitation version (`IfMatchHeader`), refuse accepted and revoked invitations with `invitation-not-open`, and answer
  `412` on a stale version. Resend creates a new link and message, supersedes the open link, increments `resend_count`, and counts
  against `Invitations:MaxResendsPerHour` (development 3), counted from the links the invitation's resends created in the last hour (the link
  created with the invitation does not count) (answer `429` with `Retry-After`). Revoke supersedes the open link and discards its unsent message.
  The list shows the delivery state of the newest link's message.
- **Rationale**: FR-013 to FR-016 and the repository convention for conditional updates.
- **Alternatives considered**: An in-memory resend limiter (rejected: lost across instances).

## R6 - Anonymous check and accept

- **Decision**: `POST /api/v1/invitations/checks` (`{ token }` → `{ clubName, accountName }`) and
  `POST /api/v1/invitations/acceptances` (`{ token, password }` → `204`), JSON-only, `Cache-Control: no-store`,
  rate limited per client with the policy `invitation-token` (`Invitations:TokenAttemptsPerMinute`, development 10).
  Every invalid case answers the neutral problem `invitation-invalid`.
- **Rationale**: FR-007, FR-008, FR-017; mirrors the recovery check and completion operations, keeping tokens out of
  URLs.
- **Alternatives considered**: Reusing the recovery rate-limit policy (rejected: its configuration belongs to recovery
  and the limits are independent).

## R7 - Audit events

- **Decision**: `invitation.created` (succeeded or refused with `email-address-in-use`, `account-name-taken`,
  `validation`), `invitation.issued`, `invitation.resent` (succeeded or refused with `rate-limited`,
  `invitation-not-open`, `version-mismatch`), `invitation.revoked` (succeeded or refused), `invitation.accepted`
  (succeeded with the created member) and `invitation.acceptance-refused` (reason `invitation-invalid`
  or `password-policy`). Resource `invitation` with the invitation id; details carry `maskedAddress` where an address
  is involved. Detail keys are added to the `PostgresAuditTrail` allow-list.
- **Rationale**: FR-019 and the masked-address rule of
  [Email Notifications → Security And Governance](../../docs/architecture/email-notifications.md#security-and-governance).
- **Alternatives considered**: Auditing link checks (rejected: checks change nothing and are rate limited, as in
  recovery).

## R8 - Message type and content

- **Decision**: A new notification audience `Invitee` and the account-message type `member-invitation`
  (`AccountMessage: true`, link path `/invitations/accept`), with English text in `Messages.en.json` (subject with the
  club name, invitation sentence, ignore note, lifetime, link). The own notification preference list omits types of
  the `Invitee` audience, and setting a preference for it is refused like an unknown type.
- **Rationale**: FR-020 and FR-021: the invitee is not a member, so the type is never a member preference.
- **Alternatives considered**: Listing it as an always-sent account message (rejected: members never receive it).

## R9 - Web UI

- **Decision**: The members area (`MembersPage`) gains an "Invite member" action that opens `InviteMemberDialog`
  (address, account name, team role rows) and an invitations section with a state filter, resend and confirmed revoke
  actions, and the delivery state; `invitationAdapter.ts` wraps the Kiota client. A public page
  `/invitations/accept` reads the token from the fragment, removes it from the address bar, checks it, shows club and
  account name and the password form, accepts, and leads to `/sign-in`; it shows one neutral message for
  `invitation-invalid`. Texts in `src/i18n/en/invitations.json`. Vitest unit tests and Playwright `simulated` and
  `platform` tests (the latter reads Mailpit).
- **Rationale**: Mirrors `EmailConfirmPage`, the recovery pages, and the adapter rule of the Web UI.
- **Alternatives considered**: A separate top-level invitations area (rejected: invitations belong to member
  administration).
