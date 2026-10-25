# Feature Specification: Analyst Manager Device Key Rotation

**Feature Branch**: `20261025-184607-manager-key-rotation`

**Created**: 2026-10-25

**Status**: Draft

**Input**: User description: "Let an active Analyst Manager replace its registered device key without losing its registration identity, as the architecture's Machine Authentication section requires: routine rotation preserves the Manager identity only after the Manager proves possession of both the registered old key and the replacement key. A lost key still cannot be recovered, escrowed, or rebound by an administrator (the registration is revoked and the host registers anew)."

## Clarifications

### Session 2026-10-25

- Q: How does the Manager authenticate a retry or recovery of a rotation that the platform already switched, given that every token and proof bound to the old key is refused immediately? → A: Both are supported: the same-proofs retry with the old key within the window, and replacement-key authentication for recovery after a restart.
- Q: May a Manager rotate to a key that its own registration used earlier and has since retired? → A: No. A key retired by rotation can never again be the replacement for that registration, and the refusal is audited with a stable reason (for example key previously retired).
- Q: Does the rotate command require the Manager process to be running and reachable through local control, or can it also run against the stopped Manager's protected registration? → A: The command is served by the running Manager through local control (tray or server); when no Manager process is running it reports that and changes nothing.
- Q: Is a rotation request that fails machine authentication of the outer request (for example a token bound to an unknown or retired key) audited as a refused rotation? → A: Yes when the request names an identifiable registration: the refusal is audited with that Manager and a stable reason; requests that cannot be tied to any registration produce only the platform's ordinary authentication logging.
- Q: Should the rotation operation be rate-limited for each Manager registration? → A: Yes. A per-registration rate limit applies, refusing excess requests with 429 and a stable code, and each throttled request is audited as a refusal; the limit is a development value.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Rotate the device key of an active Manager (Priority: P1)

A host operator wants to replace the device key of an Active Analyst Manager, on a schedule of their own or after a suspected exposure of the key's protection. They run the local-control rotate command. The Manager creates a replacement key in the same protected key store, proves possession of both the registered old key and the replacement key to the platform, and the platform switches the registration to the replacement key. The Manager keeps its identity, stamp binding, and fleet history, restores its queue access under the new key, and the old key can no longer be used for anything.

**Why this priority**: This is the whole feature. Without it, the only response to a suspected key exposure is to revoke the registration, re-pair the host, and lose the Manager's fleet history and queue state.

**Independent Test**: Register and activate a Manager, run the rotate command, and confirm that the Manager identity is unchanged, the fleet shows a new fingerprint, work and heartbeats continue under the new key, and every token, request proof, or queue credential tied to the old key is refused.

**Acceptance Scenarios**:

1. **Given** an Active Manager, **When** the host operator runs the rotate command, **Then** the Manager creates a replacement key in the same protected key store provider, the platform accepts the rotation, and the Manager reports the old and new key fingerprints and its unchanged identity.
2. **Given** a completed rotation, **When** anything presents a token or request proof that was bound to the old key, **Then** the platform refuses it immediately, with no grace period.
3. **Given** a completed rotation, **When** the Manager next works, **Then** it obtains new tokens and queue credentials with the replacement key, and the previous queue credentials are revoked at the broker.
4. **Given** a completed rotation, **When** a Club Admin opens the fleet view, **Then** the Manager appears as the same registration with the new key fingerprint, and its earlier status history is intact.
5. **Given** a rotation request whose proof of the registered old key is missing, signed by another key, expired, addressed to another audience, or whose identifier was already used, **When** it is submitted, **Then** it is refused and the registered key is unchanged.
6. **Given** a rotation request whose proof of the replacement key is missing, invalid, expired, addressed to another audience, or whose identifier was already used, **When** it is submitted, **Then** it is refused and the registered key is unchanged.
7. **Given** a rotation request that presents the registered key as its own replacement, **When** it is submitted, **Then** it is refused as not a replacement.
7a. **Given** a key that an earlier rotation of this registration retired, **When** a rotation request names it as the replacement, **Then** it is refused with the stable reason key previously retired, and the registered key is unchanged.
8. **Given** a replacement key whose protection kind is software-backed on a stamp that does not allow software keys, **When** rotation is requested, **Then** it is refused exactly as registration would refuse it, and the registered key is unchanged.
9. **Given** a Manager whose registration is revoked, unregistering, or otherwise not Active, **When** it requests rotation, **Then** the request is refused and nothing changes.
10. **Given** two rotation requests for one Manager racing each other, **When** both are processed, **Then** at most one switches the key and the other is refused as stale.
11. **Given** a Manager with no running process, **When** the host operator runs the rotate command, **Then** the command reports that no Manager is running and changes nothing; rotation is served only by the running Manager (tray or server) through local control.
12. **Given** a Manager that sends more rotation requests than its per-registration limit allows, **When** the excess requests arrive, **Then** they are refused with `429` and a stable code, the registered key is unchanged, and each is audited as a refusal.

---

### User Story 2 - Recover safely when rotation is interrupted (Priority: P1)

A crash, power loss, or network failure can interrupt rotation at any point: before the platform has switched the key, after it has switched but before the Manager learned the result, or while the local registration file is being updated. The Manager and the platform must converge on one key without operator repair and without ever leaving the Manager unable to prove its identity, so a host never has to re-register because rotation was interrupted.

**Why this priority**: Rotation that can strand a Manager is worse than no rotation. Safe recovery is what makes the operation usable after a suspected exposure, when it cannot be retried casually.

**Independent Test**: Interrupt rotation at each step (after replacement key creation, during transport, after the platform switch before the response arrives, and during the local file update), restart the Manager, and confirm it ends with the old key or the new key active consistently on both sides and keeps its identity.

**Acceptance Scenarios**:

1. **Given** rotation interrupted before the platform switched the key, **When** the Manager restarts, **Then** it still authenticates with the old key, removes the unused replacement key, and may be asked to rotate again.
2. **Given** rotation interrupted after the platform switched the key but before the Manager recorded it, **When** the Manager restarts, **Then** it detects the switch by authenticating with the replacement key (which the platform accepts as the registration's current key), completes the local update, and continues with the new key; it never needs re-registration.
3. **Given** a transport failure or lost response during rotation, **When** the Manager retries within the retry window with the same proofs authenticated by the old key, **Then** the platform treats the retry as the same rotation, returns the successful outcome, and the key is switched at most once; after a restart or beyond the window, recovery uses replacement-key authentication instead.
4. **Given** a crash while the signed local registration file is being replaced, **When** the Manager restarts, **Then** the file is either entirely the old one or entirely the new one, and a file that fails verification still fails closed.
5. **Given** a Manager that cannot reach the platform, **When** the rotate command runs, **Then** it reports the failure, changes no registered key, and clears no registration.
6. **Given** a refusal that does not follow a valid key proof, **When** it interrupts rotation, **Then** the Manager keeps its protected registration and does not treat it as inactive.

---

### User Story 3 - Audit and observe rotations (Priority: P2)

A Club Admin or security reviewer needs to know when a Manager's key changed, who or what asked for it, and whether refusals suggest an attack, without ever seeing key material.

**Why this priority**: Key changes are security-relevant events. Auditing is required but does not deliver the capability by itself.

**Independent Test**: Perform successful, refused, and replayed rotations and read the security audit events and the fleet view.

**Acceptance Scenarios**:

1. **Given** a rotation request, **When** it arrives, **Then** a "requested" audit event records the Manager, stamp, old fingerprint, and replacement fingerprint.
2. **Given** a successful rotation, **When** the switch completes, **Then** a "succeeded" audit event records the Manager, both fingerprints, and time.
3. **Given** a refused rotation, **When** it is refused, **Then** a "refused" audit event records a stable reason such as old-key proof invalid, replacement-key proof invalid, replay, software key refused, not Active, stale, not a replacement, key previously retired, or rate limited.
3a. **Given** a rotation request whose outer machine authentication fails (for example a token bound to an unknown or retired key) and that names an identifiable registration, **When** it is refused, **Then** a "refused" audit event records that Manager and a stable reason; a request that cannot be tied to any registration produces only the platform's ordinary authentication logging.
4. **Given** any rotation event, **When** it is read, **Then** it contains identifiers and fingerprints only and never key material, assertions, or tokens.
5. **Given** a Club Admin in the fleet view, **When** a Manager has rotated, **Then** the Manager's current fingerprint shown is the new one, with no new screen and no administrator rotation action.

---

### User Story 4 - A lost key is not recoverable by an administrator (Priority: P3)

When a host loses its device key, an administrator has no way to substitute a key for it. The registration is revoked and the host registers anew with a fresh pairing and approval.

**Why this priority**: This is a guarantee that must remain true, and it is verified by absence: no administrator operation, escrow, or rebind exists.

**Independent Test**: Confirm that no administrator or member operation can set or replace a Manager's key, that rotation requires a proof of the old key, and that revoking and re-registering is the only way forward for a lost key.

**Acceptance Scenarios**:

1. **Given** a Manager that lost its key, **When** a Club Admin looks for a way to replace the key, **Then** none exists; revocation is the available action.
2. **Given** a member's session of any role, **When** it calls the rotation operation, **Then** it is refused because only the Manager's own machine authentication can rotate.

---

### Edge Cases

- A Manager asks to rotate while its version is below the stamp's minimum: rotation is a security action of the Manager's own registration and is not blocked by the minimum-version refusal that applies to queue credentials and claims.
- Rotation arrives while attempts are active: attempts continue; their later heartbeats, result grants, and completions authenticate with the new key, and the attempt fences are unaffected.
- Rotation arrives while the Manager is paused or draining: it is accepted and the Manager's intent is unchanged.
- An old-key proof or a new-key proof is replayed against a later rotation: the replay is refused and audited.
- The Manager's clock is far from the platform's: assertion expiry is judged by the platform's time, and the refusal is actionable.
- The replacement key already is registered for another Manager or another registration: the rotation is refused without revealing that other registration.
- Rotation of a Manager whose replacement key is generated in a store that no longer works: the Manager refuses locally with an actionable message and changes nothing.
- The old key is later destroyed in the key store: removal happens only after the Manager records the new key, and a leftover old key is harmless because the platform no longer accepts it.
- Concurrent rotation from the CLI while the tray application is running: only one rotation runs at a time on the host, and the second command reports that one is in progress.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: An Active Analyst Manager MUST be able to replace its registered device key through an operation of its own machine-authenticated registration, without changing its Manager identity, stamp binding, fleet history, label, or state.
- **FR-002**: The platform MUST change the registered key only after validating, in one request, a proof of possession of the registered key and a proof of possession of the replacement key; a proof of only one of them MUST NOT suffice.
- **FR-003**: Each of the two proofs MUST be validated for its intended audience, its expiry, and a unique identifier that has not been used before; a reused identifier from either proof MUST be refused.
- **FR-004**: The request MUST be refused when the replacement key equals the registered key, equals another registration's key, equals a key that an earlier rotation of the same registration retired (stable reason key previously retired; a retired key can never again be the replacement for that registration), or is not a key of the accepted type. The platform MUST retain the fingerprint of every retired key of a registration for this check.
- **FR-005**: The key-protection kind the Manager claims for the replacement key MUST be recorded as claimed only, MUST NOT drive authorization, and MUST be subject to the stamp's software-key policy exactly as at registration.
- **FR-006**: The switch of the registered public key and its fingerprint MUST be atomic, in one unit of work, with optimistic concurrency, so that of concurrent rotations at most one succeeds and the rest are refused as stale.
- **FR-007**: From the moment of the switch, every access token and request proof bound to the old key MUST be refused; there MUST be no grace period.
- **FR-008**: At the switch, the Manager's queue credentials MUST be revoked at the broker and the Manager MUST be able to obtain new ones under the replacement key through the existing credential operation.
- **FR-009**: Rotation MUST be accepted only for an Active registration; for registrations that are pending, unregistering, unregistered, revoked, or expired it MUST be refused without change.
- **FR-010**: Rotation MUST NOT be callable by member sessions, administrators, or any party other than the Manager's own machine authentication, and no operation MUST exist that lets an administrator set, replace, escrow, or recover a Manager's key.
- **FR-011**: A retry of a rotation request that already succeeded (the same proofs, authenticated with the old key, within the retry window, after a lost response) MUST return the successful outcome without a second switch; any other request that presents the retired key MUST be refused. Recovery of a rotation the platform already switched, after a restart or beyond the retry window, MUST be possible by authenticating with the replacement key, which is the registration's current key; both recovery paths MUST be supported.
- **FR-012**: The platform MUST write security audit events for requested, succeeded, and refused rotations carrying the Manager, stamp, old and new fingerprints, time, correlation, and a stable refusal reason, and MUST NOT write key material, assertions, tokens, or proofs. A rotation request that fails machine authentication of the outer request MUST be audited as a refusal with that Manager and a stable reason when it names an identifiable registration; requests that cannot be tied to any registration produce only the platform's ordinary authentication logging.
- **FR-013**: Refusals MUST use the platform's standard problem responses with stable codes and MUST NOT reveal anything about other registrations.
- **FR-014**: The Manager MUST generate the replacement key in the same protected key store provider as the registered key, as a non-exportable key, and MUST refuse to rotate when that provider cannot create one; it MUST NOT fall back to an exportable or unprotected key.
- **FR-015**: The Manager MUST offer a local-control command to rotate its key, served by the running Manager (tray or server) through local control, which reports the outcome with the identity and the old and new fingerprints, and which refuses when no Active registration exists or another rotation is running. When no Manager process is running, the command MUST report that and change nothing; it MUST NOT operate on a stopped Manager's protected registration.
- **FR-016**: The Manager MUST update its signed local registration file atomically so that a crash leaves either the entire old or the entire new file, and the file MUST be signed with the key that is registered at that moment.
- **FR-017**: After any interruption (crash, restart, transport failure, lost response), the Manager MUST converge on the key the platform has registered, by determining which of its keys the platform accepts, and MUST remove the unregistered key from its store; it MUST NOT require re-registration for this.
- **FR-018**: The Manager MUST NOT treat its registration as inactive because rotation failed for transport reasons or a refusal not following a valid key proof; its protected registration stays intact.
- **FR-019**: The rotation request and responses MUST have a versioned contract with golden fixtures under the repository's Analyst Manager contracts folder, shared by the platform and the Manager tests, covering both proofs, success, retry, and every refusal reason.
- **FR-020**: The fleet read that Club Admins already use MUST show the Manager's current key fingerprint after rotation, with no new fleet screen, and the Manager's earlier status history MUST remain.
- **FR-021**: Rotation MUST NOT be blocked by the stamp's minimum Manager version, and MUST NOT disturb attempts already held, which MUST continue under the new key.
- **FR-022**: Rotation intervals, scheduling, automatic rotation, key attestation, administrator-initiated rotation, key recovery, and production key-custody values MUST NOT be introduced by this feature.
- **FR-023**: The rotation operation MUST be rate-limited per Manager registration; excess requests MUST be refused with `429` and a stable code, each throttled request MUST be audited as a refusal, and the limit is a development value.

### Key Entities

- **Registration**: The Analyst Manager's stable identity and stamp binding; gains a changing current device key and fingerprint while keeping its identity, and the fingerprints of its retired keys.
- **Rotation request**: One authenticated request carrying a proof by the registered key and a proof by the replacement key with the claimed protection kind.
- **Key proof**: A signed, audience-bound, expiring assertion with a unique identifier, used once.
- **Rotation outcome**: The result of a request (switched, already switched, or refused with a stable reason).
- **Rotation audit event**: A security event for requested, succeeded, or refused rotation with identifiers and fingerprints only.
- **Local registration file**: The Manager's signed local state, updated atomically to the key in force.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A host operator rotates the key of an Active Manager with one command in under one minute on a healthy connection, and the Manager's identity, label, and history remain the same in 100% of tested cases.
- **SC-002**: After a successful rotation, 100% of tested tokens, request proofs, and queue credentials bound to the old key are refused, with no grace period.
- **SC-003**: In every tested interruption point, the Manager and the platform converge on a single key without re-registration, and the key is switched at most once.
- **SC-004**: 100% of tested requests missing or failing either of the two proofs, replaying an identifier, presenting a non-replacement key (including a previously retired key), exceeding the rate limit, or violating the software-key policy leave the registered key unchanged and produce an audited refusal.
- **SC-005**: Concurrent rotation of one Manager switches the key at most once in 100% of tested runs.
- **SC-006**: A Club Admin sees the new fingerprint in the existing fleet view on the next read after rotation, with no new administrator action required.
- **SC-007**: No audit event, log, contract fixture, or response contains key material, assertions, or tokens, verified by tests.
- **SC-008**: No tested administrator or member operation can set, replace, or recover a Manager's key.

## Assumptions

- The rotation operation lives under the Manager's own machine-authenticated area of the API and uses the existing machine authentication for the request itself, plus the two key proofs inside it.
- The replacement key is generated in the same protected key store provider that holds the registered key; moving to another provider is a re-registration.
- Replacement-key proofs reuse the existing client-assertion format, with an audience and lifetime like the registration's own proofs.
- Rotation is allowed at any time while the registration is Active; whether a Manager ought to rotate at all, and how often, is the host operator's decision and no interval is enforced.
- The existing fleet read already exposes the key fingerprint, so no new Web UI is needed; the connected Web UI fleet view shows the new value after rotation.
- Values such as proof lifetimes and the retry window for an already-succeeded rotation are development values; production values and key custody stay with `GOV-CRED-002`, `GOV-CRED-003`, and `GOV-CRED-004`, which remain unresolved.
- Hardware key attestation and per-operating-system evidence spikes beyond the existing providers are out of scope; Windows and macOS stores are verified manually as they were for registration, and automated validation uses the software PKCS#11 token.
- Depends on the implemented Analyst Manager Registration (`specs/archive/20261005-130704-analyst-manager-registration`), Manager Work Execution (`specs/archive/20261012-141233-manager-work-execution`), and Manager Fleet Status (`specs/archive/20261012-153308-manager-fleet-status`).
- Implements Machine Authentication and Registration And Stamp Binding of `docs/architecture/analyst-manager.md` and the credential rotation expectations of `docs/architecture/analyst-runtime-and-recovery.md`.
