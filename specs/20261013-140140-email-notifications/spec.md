# Feature Specification: Member Email Addresses and Pending-Request Notifications

**Feature Branch**: `20261013-140140-email-notifications`

**Created**: 2026-10-13

**Status**: Draft

**Input**: User description: "Member Email Addresses and Pending-Request Notifications, the first use of the transactional email channel (docs/architecture/email-notifications.md). (1) Email addresses (platform API): every member may set, change, or remove one optional email address; a Club Admin may set or remove a member's address; a new or changed address is verified through a single-use, time-limited confirmation link (token stored only as a hash) before it receives anything else; a changed address stops receiving messages at once; deactivated members receive nothing; address changes, verification, and preference changes are audited with masked addresses. (2) Notification preferences per member and notification type: on by default once the address is verified; each member can turn a type off and on; account messages (address verification) are always sent. (3) First notification 'a registration request waits for approval': when a Registrar's pairing-code submission creates a pending request, every Club Admin with a verified address and the type enabled receives one email that names only the kind of event and links to the Web UI's fleet view, with no host label, fingerprint, or other data; repeated requests within a configured window are coalesced per recipient. (4) Delivery: messages are written to the transactional outbox in the same unit of work as the triggering change; a delivery worker in the API host renders them from translation keys (English) and submits them by authenticated TLS SMTP to the stamp's configured relay, retrying transient failures with bounded backoff; only minimal delivery records, never bodies; telemetry never contains full addresses or bodies; the Aspire AppHost runs a mail-catcher container as the development relay and tests use the same image. (5) Web UI (on Web UI Foundation): the account view sets, changes, and removes the address, shows its verification state, resends the verification link, and toggles notification preferences; a verification landing page confirms the token from the link; the Club Admin member view shows and sets a member's address. Out of scope: self-service password recovery and invitations, other notification types, production relay values, sender authentication (SPF/DKIM/DMARC), bounce handling, delivery-record retention, and localization beyond English. Depends on Club and Identity Foundation, Analyst Manager Registration, and Web UI Foundation. Implements docs/architecture/email-notifications.md (all sections), docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui (the email notification of pending requests), docs/architecture/security-and-data-governance.md (DAT-001 addresses, BND-010, THR-012, audit events), and docs/architecture/platform-implementation.md#api-and-identity."

## Clarifications

### Session 2026-10-13

- Q: Does opening the confirmation link verify the address immediately? → A: No. The link opens a Web UI page that asks the member to confirm with one click; only that click verifies the address, so automatic link scanners cannot verify it.
- Q: What development values apply to confirmation links, resends, and coalescing? → A: Links last 24 hours; at most 3 confirmation messages per member per hour; pending-request messages coalesce per Club Admin over 15 minutes. These are development and test values; production values stay Open / Blocking.
- Q: When is the message sent when several requests arrive within the coalescing window? → A: Immediately for the first request; later requests within the window send nothing, and the fleet's pending count shows them.
- Q: Who may see a member's email address? → A: Only the member and Club Admins; no team, fleet, or other view shows it, and no API returns it to other members.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Add and verify my email address (Priority: P1)

A member opens their account in the Web UI and enters an email address. The platform sends a confirmation message to that address. The member opens the link in the message, the Web UI confirms the address, and the account shows it as verified. Until then, the address receives nothing else. The member can change the address, which starts a new verification and stops all messages to the old one, or remove it.

**Why this priority**: Without a verified address no message can be sent; verification protects members from receiving mail at an address they do not control and the club from sending to a mistyped one. On its own, this story already gives members a verified contact address.

**Independent Test**: As a member, set an address, find the confirmation message in the development mail catcher, open its link, and see the address verified; change the address and observe that the old one receives nothing further; open an old or expired link and observe the refusal.

**Acceptance Scenarios**:

1. **Given** a signed-in member without an address, **When** they set an address, **Then** the account shows it as unverified and exactly one confirmation message is sent to it.
2. **Given** a confirmation message, **When** the member opens its link within its lifetime and confirms with one click on the page it opens, **Then** the account shows the address as verified; opening the link without confirming changes nothing.
3. **Given** a confirmation link that is expired, already used, or belongs to an address the member has since changed or removed, **When** it is opened, **Then** the Web UI shows that the link is no longer valid and offers to send a new one, and nothing changes.
4. **Given** an unverified address, **When** the member asks to resend the confirmation, **Then** a new message is sent and earlier links stop working.
5. **Given** a verified address, **When** the member changes it, **Then** the new address is unverified and receives a confirmation message, and the old address receives no further messages.
6. **Given** an address, **When** the member removes it, **Then** the account has no address and no message is sent anywhere.
7. **Given** a confirmation link opened by someone who is signed out or signed in as another member, **When** the link is valid, **Then** the address it belongs to is verified without revealing the account it belongs to, and nothing about the signed-in member changes.

---

### User Story 2 - Club Admins hear about pending Manager requests by email (Priority: P2)

When a Registrar pairs a new Analyst Manager and the request waits for a decision, every Club Admin with a verified address who has not turned this notification off receives one email. It says that a registration request waits for approval and links to the fleet view in the Web UI; it contains nothing else about the request. When several requests arrive close together, a Club Admin receives one message for them rather than many.

**Why this priority**: A request expires when nobody decides in time, and Club Admins do not watch the fleet view continuously. This story makes the channel useful. It needs verified addresses (Story 1).

**Independent Test**: With two Club Admins (one verified, one not) and a Coach (verified), submit a pairing code as a Registrar and observe exactly one message in the mail catcher, to the verified Club Admin, containing only the event and the link; submit two more within the coalescing window and observe no second message.

**Acceptance Scenarios**:

1. **Given** Club Admins with verified addresses and the notification enabled, **When** a registration request becomes pending, **Then** each receives exactly one message that names the event and links to the Web UI fleet view.
2. **Given** a Club Admin without an address, with an unverified address, with the notification turned off, or deactivated, **When** a request becomes pending, **Then** they receive nothing.
3. **Given** members without the Club Admin role, **When** a request becomes pending, **Then** they receive nothing, even with a verified address.
4. **Given** a Club Admin who received a message, **When** further requests become pending within the configured coalescing window, **Then** they receive no further message until the window has passed.
5. **Given** a message, **When** its content is inspected, **Then** it contains no host label, device fingerprint, submitting member, team, match, or player data, and its link carries no credential or token.
6. **Given** a Club Admin who loses the role or turns the notification off before the message is sent, **When** a request becomes pending, **Then** they receive nothing.

---

### User Story 3 - Choose which notifications I receive (Priority: P3)

A member with a verified address sees the notification types that apply to their roles and turns each on or off. Account messages, such as address confirmation, are always sent and cannot be turned off.

**Why this priority**: Members must be able to stop messages they do not want. It needs a verified address (Story 1) and a notification type (Story 2).

**Independent Test**: As a Club Admin with a verified address, turn the pending-request notification off, submit a pairing code, and observe no message; turn it on again and observe the next message.

**Acceptance Scenarios**:

1. **Given** a member with a verified address, **When** they open their notification settings, **Then** they see each notification type that applies to their roles, on by default, and can turn each off and on.
2. **Given** a member who turned a type off, **When** an event of that type occurs, **Then** they receive nothing.
3. **Given** a member who turned a type on again, **When** the next event of that type occurs, **Then** they receive a message.
4. **Given** account messages, **When** a member views their settings, **Then** account messages are listed as always sent and cannot be turned off.

---

### User Story 4 - Club Admins manage members' addresses (Priority: P4)

A Club Admin opens a member in the Web UI, sees whether the member has an address and whether it is verified, and sets or removes it, for example for a member who rarely uses the Web UI. A Club Admin-set address is verified the same way: the member must open the confirmation link.

**Why this priority**: It helps clubs onboard members who do not set their own address. It builds on Story 1.

**Independent Test**: As a Club Admin, set an address for a member, observe the confirmation message, open the link, and see the address verified in both the member's account and the Club Admin's member view; remove it and observe that the member has no address.

**Acceptance Scenarios**:

1. **Given** a Club Admin, **When** they open a member, **Then** they see the member's address (if any) and its verification state.
2. **Given** a Club Admin, **When** they set or change a member's address, **Then** the address is unverified and a confirmation message is sent to it, as when the member sets it.
3. **Given** a Club Admin, **When** they remove a member's address, **Then** the member has no address and receives nothing.
4. **Given** a member who is not a Club Admin, **When** they try to set another member's address, **Then** the platform refuses.

### Edge Cases

- **Same address on two accounts**: Allowed; addresses are contact data, not sign-in identifiers; each account verifies independently.
- **Invalid address format**: Refused when set, with a clear reason; nothing is sent.
- **Relay unreachable**: Messages stay queued and are retried with bounded backoff; members' actions (setting an address, approving requests) never wait for delivery.
- **Relay permanently refuses a message**: The delivery is recorded as failed and not retried; nothing else changes.
- **Member deactivated after a message was queued**: The message is not sent.
- **Address changed after a message was queued for the old address**: The message is not sent to the old address.
- **Platform restarts with queued messages**: They are delivered after the restart; a message recorded as sent is never sent again, and only a crash between the relay accepting a message and the platform recording it may cause one duplicate.
- **Very many pending requests**: Coalescing bounds messages per Club Admin to one per window.
- **Confirmation link opened twice**: The second opening shows that the address is already verified or the link is no longer valid.
- **Link opened by an automatic scanner**: The page loads but nothing is verified until a person clicks the confirmation.

## Requirements *(mandatory)*

### Functional Requirements

#### Addresses and verification

- **FR-001**: Every member MUST be able to set, change, and remove one optional email address for their own account; a Club Admin MUST be able to set, change, and remove any member's address; other members MUST be refused.
- **FR-002**: The platform MUST refuse addresses that are not syntactically valid email addresses or exceed a maximum length, with a clear reason.
- **FR-003**: A newly set or changed address MUST be unverified and MUST receive exactly one confirmation message containing a single-use, time-limited link; the platform MUST store only a hash of the link's token.
- **FR-004**: Opening a link MUST show a Web UI page that asks for one explicit confirmation; only that confirmation MUST verify exactly the address the link was issued for, so opening the link alone (for example by an automatic link scanner) changes nothing. Expired, used, superseded, or unknown links MUST be refused without revealing which applies or which account they belong to.
- **FR-005**: Requesting a new confirmation MUST invalidate earlier links for that address; confirmation messages MUST be limited per member to the configured rate (development value 3 per hour), and a request beyond it MUST be refused with the time to wait.
- **FR-006**: An unverified address MUST receive nothing except its confirmation message; a changed or removed address MUST receive nothing further, including messages already queued for it; deactivated members MUST receive nothing.
- **FR-007**: Addresses MUST NOT be usable for sign-in.

#### Preferences

- **FR-008**: Each member MUST be able to turn each notification type that applies to their roles off and on; each type MUST be on by default once the address is verified.
- **FR-009**: Account messages (address confirmation in this slice) MUST always be sent and MUST NOT be turnable off.

#### Pending-request notification

- **FR-010**: When a registration request becomes pending approval, the platform MUST create one notification per member who at that moment is an active Club Admin with a verified address and the notification type enabled.
- **FR-011**: A Club Admin MUST receive at most one pending-request message per configured coalescing window (development value 15 minutes): the message for the first request MUST be created at once, and further requests within the window MUST NOT create messages for that Club Admin; the message text MUST say that one or more requests wait, so it stays correct for the requests it covers.
- **FR-012**: The message MUST name only the event and link to the Web UI fleet view; it MUST contain no host label, fingerprint, submitting member, team, match, or player data, and its link MUST carry no credential or token.

#### Delivery

- **FR-013**: Messages MUST be created in the same transaction as the change that causes them, so they are sent only for committed changes and survive restarts.
- **FR-014**: A delivery worker MUST render each message from translation keys (English) and submit it by authenticated, TLS-protected SMTP to the stamp's configured relay; it MUST retry transient failures with bounded backoff, record permanent failures without retrying, and never send a message again once it is recorded as sent; only a crash between the relay accepting a message and the platform recording it may cause at most one duplicate.
- **FR-015**: Before sending, the worker MUST re-check that the recipient is still active, that the address the message was created for is still the member's verified address (or, for a confirmation message, the current unverified address), and, for notification types other than account messages, that the recipient still belongs to the type's audience (for the pending-request notification: still a Club Admin) and still has the type enabled; otherwise the message MUST be discarded.
- **FR-016**: The platform MUST keep only minimal delivery records (message identity, type, recipient member, state, timestamps) and MUST NOT retain rendered bodies after delivery.
- **FR-017**: In local development and tests, a mail catcher MUST act as the relay, so no message leaves the machine.

#### Web UI

- **FR-018**: The Web UI account view MUST let members set, change, and remove their address, show its verification state, resend the confirmation, and toggle notification types; a verification page MUST show the confirmation action for the token from the link and confirm it only when the member clicks it.
- **FR-019**: The Web UI member view MUST let Club Admins see a member's address and verification state and set, change, or remove it.
- **FR-020**: All new user-facing text and message text MUST come from translation keys with English as the only shipped language, and new views MUST be operable by keyboard and pass automated WCAG 2.2 AA rule checks, as the foundation requires.

#### Audit and privacy

- **FR-021**: Setting, changing, verifying, and removing an address and changing a preference MUST emit audit events with masked addresses; telemetry and logs MUST never contain full addresses, tokens, or message bodies.
- **FR-022**: Only the member and Club Admins MUST be able to read a member's address and verification state; no other API response or Web UI view (teams, fleet, registration requests, audit views) MUST contain it.

### Key Entities

- **Member email address**: Optional contact address of one member, with verification state (unverified, verified) and the time of verification; part of the identity records.
- **Email confirmation**: A single-use, time-limited confirmation for one address, stored as a hash, superseded by a newer one.
- **Notification preference**: Per member and notification type, on or off; account messages have none.
- **Notification type**: A kind of message with its audience rule (here: pending registration request, for Club Admins) and whether it is an account message.
- **Email message**: A queued message with identity, type, recipient member, the address it was created for, state (queued, sent, failed, discarded), and timestamps; no body is retained.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A member can add an address and verify it in under 2 minutes, including opening the message.
- **SC-002**: In the tests, a pending registration request produces exactly one message per eligible Club Admin and 0 messages to anyone else, and further requests within the coalescing window produce 0 additional messages.
- **SC-003**: In the tests, 0 messages are sent to unverified (except confirmation), changed, or removed addresses, or to deactivated members, including messages queued before the change.
- **SC-004**: 0 messages, audit events, logs, or telemetry in the tests contain a host label, fingerprint, credential, or full confirmation token, and 0 audit events, logs, or telemetry contain a full address or message body.
- **SC-005**: After a relay outage in the tests, every queued message is delivered exactly once once the relay is reachable again, and member actions never wait for delivery.
- **SC-006**: 100% of the acceptance scenarios are covered by automated platform and Web UI tests, and an end-to-end set against the real platform and mail catcher (set and verify an address, pair a Manager as Registrar, receive the notification as Club Admin) passes on every merge.
- **SC-007**: 0 Content Security Policy violations and 0 automated WCAG 2.2 AA violations on every new view in the browser tests.
- **SC-008**: In the tests, 0 API responses to members other than the address owner and Club Admins contain an email address.

## Assumptions

- **Actors**: Every member manages their own address and preferences; Club Admins manage members' addresses and receive the pending-request notification; Registrars trigger it by submitting pairing codes.
- **Channel rules**: The channel's rules come from the architecture (transactional only, verified addresses, minimal content, preferences on by default, outbox and SMTP delivery, mail catcher locally); this spec does not restate them beyond what it tests.
- **Configured values**: Confirmation-link lifetime (development value 24 hours), resend rate limit (development value 3 confirmation messages per member per hour), coalescing window (development value 15 minutes), retry backoff, relay endpoint and credentials, sender address, and the Web UI base address used in links are explicit configuration with development and test values; production values remain Open / Blocking under the production operations architecture.
- **Out of scope**: self-service password recovery and invitations (later specifications on the same channel); other notification types; production relay, sender domain, SPF, DKIM, and DMARC; bounce and complaint handling; retention of delivery records; languages other than English.
- **Evidence**: Passing validation is development evidence only and does not establish deployment support or production readiness.
- **Dependencies**: `specs/archive/20261005-130701-club-identity-foundation` provides members, roles, sessions, audit, and the outbox's transactional base; `specs/archive/20261005-130704-analyst-manager-registration` provides pairing-code submission and pending requests; `specs/20261012-150657-web-ui-foundation` provides the Web UI account and member views, the generated client, and browser-test setup; it depends on `specs/archive/20261012-145505-web-ui-environment`.
- **Architecture References**: [docs/architecture/email-notifications.md](../../docs/architecture/email-notifications.md), [docs/architecture/analyst-manager.md](../../docs/architecture/analyst-manager.md) (Fleet Visibility In The Web UI), [docs/architecture/security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md) (Data Classes `DAT-001`; Trust Boundaries `BND-010`; Threat Model `THR-012`; Audit Events), [docs/architecture/platform-implementation.md](../../docs/architecture/platform-implementation.md) (API And Identity), [docs/architecture/production-operations.md](../../docs/architecture/production-operations.md) (Versioned Production Profile, Email).
