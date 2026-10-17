# Email Notifications

This document defines the email channel through which a deployment stamp sends
transactional messages to club members. Member email addresses, their
verification, notification preferences, outbox-backed delivery, and the
pending-request notification of Analyst Manager registration requests are
implemented as development evidence against a local mail catcher. Self-service
password recovery and invitations are not implemented, and production delivery
values remain **Open / Blocking** (see [Production Values](#production-values)).

## Purpose And Scope

Email carries only transactional account and workflow messages:

- **Workflow notifications** that ask a member to act. The first is the
  notification of Club Admins about Analyst Manager registration requests
  that wait for their decision (see
  [Fleet Visibility In The Web UI](analyst-manager.md#fleet-visibility-in-the-web-ui)).
- **Account messages**: verification of a member's email address and, each
  through its own later specification, self-service password recovery and
  invitations.

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
- Account messages (address verification and, once specified, password
  recovery) are always sent and cannot be turned off.
- Recipients are resolved when the message is created, from current roles and
  memberships: a member who lost the role that a notification addresses
  receives no further messages of that type.

## Message Content

- A notification names only the kind of event and links to the Web UI view
  where the member can act, built from the stamp's Web UI base address.
- Messages contain no host labels, device fingerprints, team, match, or player
  data, recordings, results, or agent content.
- Notification links carry no credentials or tokens. Account messages that need
  a single-use token (verification, and later password recovery) define it in
  their specification; such tokens are short-lived, single-use, and stored only
  as hashes.
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
  recipient member, state, and timestamps) and does not retain rendered message
  bodies after delivery.
- Coalescing of repeated notifications and per-recipient rate limits are
  explicit configuration. The first notification type, pending Analyst Manager
  registration requests, sends its first message at once and suppresses
  further ones for the same Club Admin within a coalescing window. Address
  confirmations are limited per member per hour.
- In local development, the Aspire AppHost runs a mail-catcher container
  (Mailpit) as the stamp's relay, and automated tests use the same pinned image
  as a disposable container. No message leaves the developer's machine.

## Security And Governance

- Submission to the relay crosses the trust boundary `BND-010` (platform to
  external email relay). Messages become copies outside the stamp's control,
  which is why their content stays minimal.
- Setting, changing, verifying, and removing an address and changing
  preferences emit audit events with masked addresses; delivery outcomes are
  counted and logged without full addresses or message bodies (`THR-008`).
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

Return to the [architecture index](README.md).
