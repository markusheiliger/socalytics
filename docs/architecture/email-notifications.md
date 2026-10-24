# Email Notifications

This document defines the email channel through which a deployment stamp sends
transactional messages to club members. Member email addresses, their
verification, notification preferences, outbox-backed delivery, and the
pending-request notification of Analyst Manager registration requests are
implemented as development evidence against a local mail catcher, and so is
self-service password recovery, and so are member invitations by email.
Production delivery values remain **Open / Blocking** (see
[Production Values](#production-values)).

## Purpose And Scope

Email carries only transactional account and workflow messages:

- **Workflow notifications** that ask a member to act. The first is the
  notification of Club Admins about Analyst Manager registration requests
  that wait for their decision (see
  [Fleet Visibility In The Web UI](analyst-manager.md#fleet-visibility-in-the-web-ui)).
- **Account messages**: verification of a member's email address,
  self-service password recovery (see [Password Recovery](#password-recovery);
  implemented as development evidence), and invitations (see
  [Member Invitations](#member-invitations); implemented as development
  evidence).

The platform never sends marketing, newsletters, digests, or bulk messages.
Every message type is introduced by the specification that needs it; this
document defines the rules all of them follow.

## Email Addresses

- An email address is optional for every member. The member sets, changes, or
  removes their own address; a Club Admin may set or remove a member's address.
- An address receives nothing except its verification message until it is
  verified. Verification uses a single-use, time-limited confirmation link,
  stored only as a hash, in the same way as Club Admin-issued set-password
  credentials. The delivery worker generates the token when it sends the
  message, so the raw token is never stored. The token travels in the link's
  URL fragment, so it never reaches server logs. The link opens a Web UI page
  that verifies the address only when the member confirms there with one
  click, so automatic link scanners cannot verify it. Changing an address requires verifying the new one; the
  previous address stops receiving messages at once.
- An address is contact data, never a sign-in identifier: members keep signing
  in with their account name. Only the member and Club Admins can read it.
- A deactivated member receives no messages.
- Addresses and their verification state belong to the identity records
  (`DAT-001` in [Security and Data Governance](security-and-data-governance.md#data-classes))
  and follow their lifecycle policy (`POL-001`).

## Notification Preferences

- Each notification type is on by default for a member once their address is
  verified, and each member can turn each type off and on again.
- Account messages (address verification and password recovery; see
  [Password Recovery](#password-recovery)) are always sent and
  cannot be turned off. Invitations (see
  [Member Invitations](#member-invitations)) are always sent as well, but they
  address a person who is not yet a member, so they never appear among a
  member's preferences.
- Recipients are resolved when the message is created, from current roles and
  memberships: a member who lost the role that a notification addresses
  receives no further messages of that type.

## Message Content

- A notification names only the kind of event and links to the Web UI view
  where the member can act, built from the stamp's Web UI base address.
- Messages contain no host labels, device fingerprints, team, match, or player
  data, recordings, results, or agent content.
- Notification links carry no credentials or tokens. Account messages that need
  a single-use token (verification, password recovery, and invitations) define
  it in their specification (for verification, see
  [Email Addresses](#email-addresses); for password recovery, see
  [Password Recovery](#password-recovery); for invitations, see
  [Member Invitations](#member-invitations)); such tokens are short-lived,
  single-use, and stored only as hashes.
- Message text comes from translation keys, as the Web UI's does; English is
  the first language.

## Delivery

- A state change that triggers a message writes the message request into a
  dedicated transactional email outbox table in the platform database, in the
  same unit of work, so a message is sent only for committed changes and never
  lost when the API restarts. Email does not use the NATS-published platform
  outbox, because it needs no broker.
- A delivery worker in the API host polls that table, re-checks before sending
  that the recipient is still active, that the address is still the one the
  message was created for, and, for notifications other than account messages,
  that the recipient still belongs to the notification's audience and still
  has it enabled. It renders each message and
  submits it by SMTP to the relay configured for the stamp. Submission
  requires TLS and authentication; the channel is provider-neutral and uses no
  provider-specific API. The platform uses the MailKit SMTP client.
- Delivery retries transient failures with bounded backoff and records a
  permanent failure without retrying further. Each message has a stable
  identity, and a message recorded as sent is never sent again; only a crash
  between the relay accepting a message and the platform recording it may cause one duplicate.
- The platform keeps only minimal delivery records (message identity, type,
  recipient member (for invitations, the invitation), state,
  and timestamps) and does not retain rendered message
  bodies after delivery.
- Coalescing of repeated notifications and per-recipient rate limits are
  explicit configuration. The first notification type, pending Analyst Manager
  registration requests, sends its first message at once and suppresses
  further ones for the same Club Admin within a coalescing window. Address
  confirmations are limited per member per hour.
- In local development, the Aspire AppHost runs a mail-catcher container
  (Mailpit) as the stamp's relay, and automated tests use the same pinned image
  as a disposable container. No message leaves the developer's machine.

## Password Recovery

Self-service password recovery is implemented as development evidence against
the local mail catcher, as follows. It is an account message for members who sign in with a local password; Club
Admin-issued reset credentials and break-glass recovery (see
[API And Identity](platform-implementation.md#api-and-identity)) stay
unchanged.

- From the Web UI sign-in page, anyone can request a recovery for an email
  address. Every well-formed request performs the same lookup and receives the
  same generic answer, no earlier than a configured minimum duration after
  receipt, so the answer reveals neither whether the address belongs to a
  member nor whether that member is eligible.
- Only an active member with a local password whose current address is
  verified and matches the request receives a message. When several eligible
  members share the address, each receives its own message whose link applies
  only to that member. Recovery never creates a local password, never changes
  links to external identity providers, and never reactivates an account, so a
  member who signs in only through a provider recovers at the provider.
- The request writes a recovery record and the message into the transactional
  email outbox in one unit of work. As for verification, the delivery worker
  re-checks eligibility, generates the single-use token when it sends the
  message, stores only its hash, and carries it in the link's URL fragment.
- A newer request supersedes the member's older links. A link stops working
  when it expires, is used, or when the member's security stamp (any password
  change), address, address verification, or membership changes; every such
  case fails with one neutral answer.
- The link opens a public Web UI page that checks the token without using it,
  shows the account name the link resets, and accepts a new password under the
  existing policy. Completing it replaces the password, clears a pending
  password-change requirement, uses the token, and ends every session of the
  member; it creates no session and does not lift a sign-in lockout.
- Recovery messages are limited per member per hour, counted from the delivery
  records; requests and token checks are limited per client. Excess requests
  for a member still receive the generic answer; excess client traffic is
  refused with the time to wait.
- Requests, issued messages, completions, and refused completions emit audit
  events with masked addresses and never contain the token, password, or full
  address.

## Member Invitations

Invitations by email are implemented as development evidence, as follows. They are
an account message through which a Club Admin offers membership to a person;
creating a member directly with a handed-over set-password or link credential
(see [API And Identity](platform-implementation.md#api-and-identity)) stays
unchanged.

- Only Club Admins invite. An invitation names one email address, the account
  name the Club Admin chooses for the new member, and optional team roles
  (`Coach` or `Viewer` per team). It is a pending record, not a member: no
  member account, role assignment, or member address exists until the invitee
  accepts, so an invitee can never sign in, hold a session, or receive other
  messages before acceptance.
- A pending invitation, also once its link expired, reserves its account
  name and address until it is accepted or revoked. Inviting an address that
  is the current address of a member or of another such invitation, or an
  account name that is taken or reserved, is refused with the conflict only,
  which a Club Admin may already know.
- Creating an invitation writes it and its message into the transactional
  email outbox in one unit of work. The delivery worker re-checks that the
  invitation is still pending and that the message carries its current link,
  generates the single-use token when it sends the message, stores only its
  hash, and carries it in the link's URL fragment, as for
  verification and recovery. The message names only the club and the link's
  lifetime.
- The link opens a public Web UI page that checks the token without using it,
  shows the club and the account name, and accepts a password under the
  existing policy. Accepting creates the active member with that account name
  and password, records the invited address as verified (receipt of the link
  proves control of it), assigns the invited team roles, uses the token, and
  creates no session. Acceptance always sets a local password; a Club Admin
  links an external identity afterwards with the existing link credential.
- Club Admins list invitations as pending, expired (the current link's
  lifetime passed), accepted, or revoked, with the delivery state of the latest
  message. They can resend a pending or expired invitation, which sends a new
  link and invalidates the previous one, and revoke a pending or expired one,
  which releases its reservations; both are conditional on the version last
  seen.
  Expired, superseded, revoked, used, and unknown links fail with one neutral
  answer.
- Resends are limited per invitation per hour, and link checks and
  acceptances per client. Creation, issued messages, resends, revocations,
  acceptances, and refused acceptances emit audit events with masked
  addresses and never contain the token, password, or full address.

## Security And Governance

- Submission to the relay crosses the trust boundary `BND-010` (platform to
  external email relay). Messages become copies outside the stamp's control,
  which is why their content stays minimal.
- Setting, changing, verifying, and removing an address and changing
  preferences emit audit events with masked addresses (password-recovery and
  invitation events follow the same rule; see [Password Recovery](#password-recovery)
  and [Member Invitations](#member-invitations));
  delivery outcomes are counted and logged without full addresses or message
  bodies (`THR-008`).
- Threat `THR-012` covers spoofed or misdirected platform mail. Minimal
  content, verified addresses, no credentials in notification links, and the
  production sender authentication below are its controls.

## Production Values

These values are **Open / Blocking** for production and are recorded in the
[Versioned Production Profile](production-operations.md#versioned-production-profile):

- the relay or provider, its location and residency, and its credentials
- the sender domain and address, and SPF, DKIM, and DMARC for that domain
- bounce and complaint handling, and when an address stops receiving messages
- retention of delivery records
- rate limits and coalescing windows
- the password-recovery link lifetime, the minimum answer duration of recovery
  requests, and the recovery message and client limits (development values
  exist; see [Password Recovery](#password-recovery))
- the invitation link lifetime, the resend limit per invitation, and the
  per-client limit of invitation link checks and acceptances (development
  values exist in the `Invitations` options; see
  [Member Invitations](#member-invitations))

Return to the [architecture index](README.md).
