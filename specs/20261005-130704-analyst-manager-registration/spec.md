# Feature Specification: Analyst Manager Registration

**Feature Branch**: `20261005-130704-analyst-manager-registration`

**Created**: 2026-10-05

**Status**: Draft

**Input**: User description: "Analyst Manager Registration: establish the first testable Analyst Manager slice. A host joins exactly one deployment stamp through a device-bound registration that a Registrar initiates and a Club Admin approves, keeps that identity in protected local storage, restores it safely after restart, exposes local operating controls (pause, resume, drain, safe exit, unregister) without a production desktop UI, verifies the container runtime before it could ever accept work, and loses all authority immediately when revoked. Work acquisition and Analyst execution are not part of this feature."

## Clarifications

### Session 2026-10-07

- Q: Which verification environment must exist before this feature starts? → A: The combined environment feature `specs/20261007-115855-environment-verification-coverage`, which extends the automated verification to the repository-root contracts folder and the Analyst Manager solution, is reviewed and merged first.
- Q: How does the host operator run and control the Analyst Manager? → A: As a desktop tray application that runs in the context of the signed-in user on Windows, macOS, and Linux (no operating-system service); its tray menu offers status, pause, pause for a chosen time with automatic resume, resume, safe exit, register, and unregister, with a status window where the desktop has no tray area; it starts automatically at the user's sign-in when the operator enables that.
- Q: Which protected stores hold the device key and local state on each operating system? → A: A per-user non-exportable key in the operating system's key store on Windows, in the Keychain (Secure Enclave when available) on macOS, and in a PKCS#11 token (for example TPM-backed) on Linux; the local registration state is a file readable only by the user's account and signed with the device key, so any change or copy to another machine is detected.
- Q: How is drift between the Manager and the platform caught without an automated end-to-end test across both? → A: Both sides are verified against shared reference examples of every registration exchange (signed proofs, credential requests, and responses), plus a manual end-to-end run before merge.
- Q: How are Registrars and Club Admins protected from approving a stranger's Manager with a phished pairing code? → A: The Manager shows a short device fingerprint that the Registrar must enter with the pairing code, and approvers see the fingerprint, request origin and time, and a warning.
- Q: May approval or policy rely on the key-protection kind the Manager reports? → A: No; it is recorded and shown as claimed only, and software-backed keys are refused unless the stamp explicitly allows them (development and test by default).
- Q: When a local unregister is cancelled because the drain timeout expires, what state do the platform and the Manager end up in? → A: The Manager asks the platform to unregister only after a successful drain; a cancelled unregister leaves the registration Active and returns the Manager to its previous Running or Paused intent.
- Q: Must the feature wait for a person's review before merging so the manual Windows, macOS and end-to-end results can be attached? → A: Yes; the pull request is always held for review and merges only after the manual Windows, macOS and end-to-end results are recorded on it.
- Q: Does this feature run in parallel with Recording Lineage and Upload or after it? → A: After it; Recording Lineage and Upload is merged first, so this feature takes the next free migration number on an up-to-date main and extends the contracts folder that Recording creates.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Register an Analyst Manager with the club's stamp (Priority: P1)

A host operator installs the Analyst Manager on a machine that should contribute compute to the club. The unregistered Manager creates a device key that cannot leave the machine's protected store, then shows a platform verification address and a short-lived pairing code. A Registrar signs in through the normal browser sign-in, submits the code, and thereby creates a pending registration request. A Club Admin approves (or rejects) the request. The waiting Manager then proves possession of its device key against a fresh platform challenge, and only that proof makes the registration Active and gives the Manager its stable identity.

**Why this priority**: Without an Active, device-bound registration an Analyst Manager can do nothing. Every later Analyst capability depends on a trustworthy machine identity that is bound to exactly one stamp and approved by accountable humans.

**Independent Test**: Can be fully tested by starting an unregistered Manager against a stamp, submitting its pairing code as a Registrar, approving as a Club Admin, and confirming that the Manager reaches Active with a stable identity, while every rejected, expired, unauthorized, or replayed path leaves it without operational access.

**Acceptance Scenarios**:

1. **Given** an unregistered Analyst Manager on a host with a supported protected store, **When** the host operator starts registration for a target stamp, **Then** the Manager creates a non-exportable device key, obtains a single-use pairing code bound to that key and stamp, and shows the verification address and code without ever asking for human credentials.
2. **Given** a valid unexpired pairing code, **When** an authenticated Registrar submits it, **Then** exactly one pending registration request bound to the device key is created and the code can never be used again.
3. **Given** a pending request, **When** a Club Admin approves it and the Manager then signs a fresh challenge with the paired key within the activation window, **Then** the registration becomes Active, the Manager receives a stable identity bound to the stamp and key, and short-lived credential issuance becomes possible.
4. **Given** a pending request has been approved, **When** the Manager has not yet completed a valid fresh proof, **Then** the platform issues no operational credential.
5. **Given** a pending request, **When** a Club Admin rejects it with a reason, **Then** the request becomes Rejected and can never be approved or activated; a new attempt requires a new pairing code and a new request.
6. **Given** a pairing code, pending request, or approved request whose time window has elapsed, **When** anyone submits, approves, or activates it, **Then** the operation is refused, the request is Expired, and retrying requires a new request with its own audit trail.
7. **Given** a user with the Registrar role but not the Club Admin role, **When** that user tries to approve or reject a pending request, **Then** the decision is refused, the request stays pending, and the denial is audited.
8. **Given** a single Club Admin, **When** that person both submits the pairing code and approves the resulting request, **Then** both operations succeed and are recorded as two separately authorized audit events.
9. **Given** an invalid, expired, already consumed, or unknown pairing code, **When** it is submitted, **Then** the refusal looks identical in every case and reveals nothing about whether a request exists.
10. **Given** a host without a supported protected store, **When** the host operator starts registration, **Then** registration is refused with an actionable explanation and no pairing code, exportable key, or partial registration is created.
11. **Given** an activation proof that reuses a consumed challenge or is signed by a different key, **When** it is presented, **Then** activation is refused and the registration does not become Active.

---

### User Story 2 - Restore the registration safely after restart (Priority: P2)

A registered Analyst Manager is restarted, for example after a reboot or an operating-system autostart. It restores its Manager identity, platform endpoint, stamp binding, device-key reference, and last operating intent from protected local storage, re-proves its device key, and confirms with the platform that the registration is still Active before doing anything operational. If the protected state is missing, unreadable, or tampered with, it fails closed instead of guessing.

**Why this priority**: A registration that cannot survive restart forces humans to re-approve every reboot, and a registration that restores unsafely would let copied or altered state impersonate a Manager. Safe restoration is the second most important property after registration itself.

**Independent Test**: Can be fully tested by restarting an Active Manager with intact, missing, unreadable, and altered protected state and observing that only intact state restores the same identity after platform confirmation, while every other case leaves the Manager non-admitting with a diagnostic.

**Acceptance Scenarios**:

1. **Given** an Active Manager with intact protected state, **When** it restarts, **Then** it restores the same Manager identity and stamp binding, obtains fresh short-lived credentials only after proving key possession, and confirms Active status before any operational action.
2. **Given** a Manager that was deliberately Paused, **When** it restarts, **Then** it comes back Paused and admits no work until a host operator explicitly resumes it.
3. **Given** a Manager that was Running, **When** it restarts, **Then** it stays non-admitting until protected state, Active registration status, and runtime preflight are all confirmed, and only then returns to Running.
4. **Given** protected state or the referenced device key that is missing, unreadable, or inaccessible to the Manager's process account, **When** the Manager starts, **Then** it admits no work, obtains no credentials, reports a bounded diagnostic without exposing protected values, and does not recreate or silently rebind the previous identity.
5. **Given** protected state that fails its integrity check or names a different stamp than the platform registration, **When** the Manager starts, **Then** it fails closed exactly as for missing state and the platform refuses any request that does not match the registered stamp and key.
6. **Given** a Manager whose registration was revoked while it was offline, **When** it restarts, **Then** it learns of the revocation, clears its local stamp state, and returns to Unregistered rather than restoring the revoked identity.
7. **Given** a Manager that is restarting, **When** the platform is temporarily unreachable, **Then** the Manager keeps its protected registration, stays non-admitting, and retries the status check with bounded backoff.

---

### User Story 3 - Revoke or unregister an Analyst Manager (Priority: P3)

A Club Admin revokes an Analyst Manager from the stamp at any time, for example because the machine was lost or compromised. Revocation takes effect on the platform immediately, regardless of whether the Manager is reachable. Separately, a host operator can unregister the Manager locally: it stops taking work, drains, asks the platform to revoke the registration, deletes its stamp credentials and cached stamp data, and returns to Unregistered.

**Why this priority**: Being able to remove a machine's authority immediately is the essential security counterweight to registration. Local unregister lets a host leave cleanly or move to another stamp.

**Independent Test**: Can be fully tested by revoking an Active Manager and confirming that every later Manager request is refused and the Manager returns to Unregistered once it observes the revocation, and by running local unregister and confirming the platform registration is revoked and local stamp state is removed.

**Acceptance Scenarios**:

1. **Given** an Active Manager, **When** a Club Admin revokes it, **Then** every subsequent Manager request and credential renewal is refused immediately, whether or not the Manager is connected.
2. **Given** a revoked Manager that is connected, **When** its next status check or any platform request is refused because of revocation, **Then** it clears its stamp credentials and cached stamp data, enters Revoked, and then returns to Unregistered, ready for fresh registration.
3. **Given** an Active Manager that is Running, Paused, Runtime unavailable, or Draining, **When** a Club Admin revokes it, **Then** revocation takes precedence over the local operating state.
4. **Given** an Active Manager, **When** the host operator unregisters it locally, **Then** it stops new work, drains, requests revocation, removes its stamp credentials and cached stamp data from protected storage, and returns to Unregistered.
5. **Given** a Manager that was revoked or unregistered, **When** the host operator wants to join the same or another stamp, **Then** a completely new pairing, Registrar submission, and Club Admin approval is required; the old registration is never edited or reactivated.
6. **Given** a Manager whose device key was lost, **When** the host operator wants it to participate again, **Then** no administrator can recover or rebind the old key; the old registration is revoked and the host registers anew.

---

### User Story 4 - Verify the container runtime before the Manager may run (Priority: P4)

After its registration is Active, and on every start, the Analyst Manager checks the host's container runtime before it may enter Running. If any check fails, the Manager stays registered in Runtime unavailable, keeps reporting status and observing revocation, advertises no unvalidated capability, and tells the host operator which check failed and how to fix it.

**Why this priority**: A Manager must never claim capabilities the host cannot deliver. Preflight is the gate between an Active registration and readiness for future work, but it delivers value only once registration exists.

**Independent Test**: Can be fully tested with a simulated container runtime that is made healthy, unreachable, unsupported, or missing an accelerator, observing the Manager's resulting operating state, reported checks, and advertised capabilities.

**Acceptance Scenarios**:

1. **Given** an Active Manager and a healthy, supported runtime, **When** preflight runs, **Then** every check passes and the Manager enters its restored intent (Running or Paused).
2. **Given** an Active Manager and a runtime that is unreachable, not local, not authorized, outside the supported compatibility list, or lacking the required container backend, **When** preflight runs, **Then** the Manager enters Runtime unavailable, admits no work, advertises no capability, and reports the failed check with remediation guidance.
3. **Given** a host where one advertised accelerator fails its functional device probe, **When** preflight runs, **Then** that accelerator is not reported as a validated capability.
4. **Given** a Manager in Runtime unavailable, **When** a Club Admin revokes it or the host operator asks for status, **Then** revocation is still observed and status is still reported.
5. **Given** a Running or Paused Manager, **When** the runtime becomes unavailable or is upgraded outside the supported compatibility list, **Then** the Manager moves to Runtime unavailable and returns to its prior Running or Paused intent only after preflight passes again.
6. **Given** no supported runtime integration is configured, **When** the Manager starts, **Then** it remains Runtime unavailable and does not discover or load any external runtime extension.

---

### User Story 5 - Control the Manager's local operating state (Priority: P5)

The host operator controls an Active Manager from its tray menu (or its status window where the desktop has no tray area): pause new work, pause for a chosen time, resume, drain and exit safely, or unregister, and view the current status. Pausing and resuming never change the registration itself, and a deliberate pause survives restart.

**Why this priority**: Operators need predictable control over a machine that contributes compute, but these controls matter only once registration, restoration, and preflight exist.

**Independent Test**: Can be fully tested by issuing pause, timed pause, resume, safe-exit, and unregister commands to the Manager's worker without an interactive desktop session with simulated active work and observing operating-state transitions, admission behavior, persisted intent, and drain-timeout outcomes.

**Acceptance Scenarios**:

1. **Given** an Active Running Manager, **When** the host operator pauses it, **Then** new work admission stops immediately while the Manager stays registered, connected, observes revocation, and keeps updating its status.
2. **Given** a Paused Manager whose last preflight passed, **When** the host operator resumes it, **Then** it returns to Running with the same registration identity.
3. **Given** a Manager with simulated active work, **When** the host operator requests safe exit and the work finishes before the drain timeout, **Then** the Manager exits without revoking its registration and keeps its last Running or Paused intent for the next start.
4. **Given** a Manager with simulated active work that does not finish, **When** the configured drain timeout expires, **Then** the configured timeout policy either cancels the exit or unregister request or stops and cleans up the remaining Manager-owned work, and the Manager never stays in Draining indefinitely; a cancelled unregister has not contacted the platform, so the registration stays Active and the Manager returns to its previous Running or Paused intent.
5. **Given** an Active Manager, **When** the host operator asks for status, **Then** it reports registration state, stamp, club, connectivity, operating state, uptime, and the latest preflight result without any secret values.
6. **Given** a Manager in Runtime unavailable, **When** the host operator pauses it, **Then** the Paused intent is recorded and the Manager enters Paused, not Running, once preflight passes.
7. **Given** an Active Running Manager, **When** the host operator pauses it for a chosen time, **Then** admission stops immediately, status shows when the pause ends, and when that time is reached the Manager resumes Running automatically if the registration is Active and the latest preflight passed.
8. **Given** a timed pause that has not yet ended, **When** the Manager restarts or the host operator resumes early, **Then** after a restart the pause continues until its original end time, and an early resume ends it immediately.
9. **Given** autostart is enabled by the host operator, **When** the user signs in to the host, **Then** the Manager starts in that user's context without administrator rights and restores its registration as for any other start; **and When** autostart is disabled, **Then** it no longer starts at sign-in.

---

### Edge Cases

- The same pairing code is submitted twice, or by two Registrars at once: exactly one pending request is created and the other submission receives the same indistinguishable refusal as an unknown code.
- The Club Admin approves after the pending-request window has elapsed, or the Manager proves key possession after the activation window has elapsed: the request is Expired and nothing is activated.
- A Club Admin loses the Club Admin role before deciding: the later approval or rejection is refused because authorization is checked at decision time.
- The Manager restarts or is closed while waiting for a code submission, approval, or activation: pairing secrets are never persisted, so the in-progress pairing is abandoned, the platform request expires, and the host operator starts a new pairing. The device key created for that attempt may remain unreferenced in the protected store; it is never used again, carries no authority, and an operator may remove it (keys named `SocAlytics-*`).
- Protected local state is copied to another machine: for hardware-backed keys the private key cannot leave the device, so the copy cannot prove possession and the platform refuses it; software-backed keys may follow a roaming user profile, which is why they are refused unless explicitly allowed (FR-041).
- Someone tricks a Registrar into submitting a pairing code for an attacker's machine: without the matching device fingerprint shown on that machine the submission is refused, and approvers see the pairing request's origin and a warning before deciding.
- Local configuration or a request names a different stamp than the registration: the request is refused and no cross-stamp operation occurs.
- The protected store becomes inaccessible while the Manager is running (for example, after a change of the Manager's process account): the Manager stops admitting work, reports a diagnostic, and does not fall back to unprotected storage.
- A Club Admin revokes the Manager while it is offline: the platform refuses it immediately; the Manager clears its local stamp state when it next reaches the platform.
- Revocation arrives while the Manager is Draining for safe exit or unregister: revocation takes precedence and the Manager clears stamp state immediately.
- The desktop has no tray area (for example a Linux desktop without status-notifier support): the Manager shows its status window with the same controls instead of failing to start.
- A timed pause ends while the Manager is Runtime unavailable: the Running intent is restored, but the Manager stays Runtime unavailable and admits no work until preflight passes.
- The user signs out or the host shuts down: the Manager performs a safe exit within the time the operating system allows, keeps its registration and last operating intent, and restores both at the next start.
- Local state is copied to another user account or machine, or edited: its signature does not verify with that account's device key, so the Manager fails closed.
- Local unregister cannot reach the platform: the Manager stays non-admitting and Unregistering, keeps retrying the revocation request, reports that unregister is pending, and completes when the platform confirms or when a Club Admin revocation is observed.
- The platform is unreachable at startup: the Manager keeps its protected registration, admits no work, and retries status checks with bounded backoff.
- A runtime is detected but its product version is not on the supported compatibility list: preflight fails; detection alone never makes a runtime supported.
- Pairing-code guessing or polling floods: requests beyond the rate limit are refused without revealing whether any request exists.
- Clock differences between host and platform: all expiry decisions are made by the platform.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: An Analyst Manager without an Active registration MUST NOT discover, request, dequeue, or execute work and MUST NOT obtain operational credentials.
- **FR-002**: Before requesting pairing, the Analyst Manager MUST hold an asymmetric device key in a supported operating-system-protected store from which the private key cannot be exported. When no supported protected store is available, the Manager MUST refuse to register with an actionable explanation and MUST NOT fall back to an exportable key file or unprotected storage.
- **FR-003**: Each registration MUST target exactly one deployment stamp. The pairing request MUST bind the device public key, the target stamp, a fresh nonce, an expiry, and minimized reported device metadata.
- **FR-004**: The platform MUST issue a high-entropy, single-use, narrowly scoped pairing code and polling handle for each pairing request. The pairing code MUST expire after the configured pairing window. The Manager MUST show the platform verification address and the pairing code to the host operator.
- **FR-005**: Human sign-in for pairing, approval, rejection, and revocation MUST happen through the platform's normal browser sign-in. Human credentials and human session tokens MUST NOT pass through, be requested by, or be stored by the Analyst Manager.
- **FR-006**: Only an authenticated user holding the Registrar capability (which Club Admins inherit) MUST be able to submit a pairing code. A valid submission MUST consume the code and create exactly one key-bound pending registration request for the stamp's club.
- **FR-007**: The platform MUST refuse invalid, expired, consumed, and unknown pairing codes and polling handles with one outwardly indistinguishable result and MUST rate-limit pairing-code submissions and polling.
- **FR-040**: The Analyst Manager MUST show, next to the pairing code, a short device fingerprint derived from its device public key; the Registrar MUST enter that fingerprint together with the pairing code, the platform MUST refuse a submission whose fingerprint does not match the paired key (with the same indistinguishable refusal as an invalid code), and the Club Admin decision view MUST show the fingerprint, the time and network origin of the pairing request, and a warning to approve only Managers the approver can physically identify.
- **FR-041**: The key-protection kind the Manager reports (hardware-backed, Secure Enclave, PKCS#11 token, or software) MUST be recorded and shown as claimed by the Manager, MUST NOT be used as an authorization or policy decision, and software-backed keys MUST be refused at registration unless the stamp configuration explicitly allows them (allowed only for development and test by default).
- **FR-008**: Only a user holding the Club Admin role MUST be able to approve or reject a pending request; a rejection MUST record a reason. Submission and decision MUST be separately authorized operations, even when the same Club Admin performs both, and authorization MUST be evaluated at the time of each operation.
- **FR-009**: A pending request MUST remain available for a Club Admin decision for the configured approval window after code consumption and MUST become Expired when that window elapses.
- **FR-010**: Approval MUST NOT grant operational access. The platform MUST activate an approved request only after the waiting Manager signs a fresh, single-use, request-bound platform challenge with the paired private key within the configured activation window. Stale, replayed, or mismatched proofs MUST be refused.
- **FR-011**: Successful activation MUST assign a stable Manager identity bound to the target stamp and the device public-key thumbprint and MUST make the registration Active.
- **FR-012**: Rejected, Expired, Revoked, and Unregistered registrations MUST NOT be approved, activated, or restored to Active. Every retry MUST create a new request with its own audit trail, and rebinding to another stamp MUST require unregister or revocation followed by fresh pairing and approval; an existing registration MUST NOT be edited to change its stamp or key.
- **FR-013**: An Active Manager MUST prove possession of its registered device key to obtain credentials and MUST receive only short-lived, proof-of-possession-bound credentials scoped to its Manager identity, its registered stamp, and Manager operations. These credentials MUST NOT authorize human or administrative operations.
- **FR-014**: The platform MUST validate every Manager request against the authoritative registration state, the registered stamp, and the registered key, MUST refuse replayed proofs, and MUST NOT let local configuration or request contents override the stamp binding.
- **FR-015**: Pairing, credential issuance, and every Manager–platform exchange MUST use authenticated, encrypted transport; unencrypted connections MUST be refused.
- **FR-016**: The platform MUST persist registration requests, registrations, and their lifecycle state durably so that they survive platform restarts.
- **FR-017**: The Analyst Manager MUST keep its Manager identity, platform endpoint, stamp binding, device-key reference, and last operating intent (Running, Paused, or Paused until a time) only in local state that is readable and writable solely by the user account running the Manager and that is signed with the device key, so that any change, or a copy to another account or machine, fails verification. It MUST NOT persist human credentials, access credentials, proofs, pairing codes, polling handles, broker secrets, or private-key material outside the protected key store.
- **FR-018**: On every start, including operating-system autostart, the Analyst Manager MUST restore its protected registration, prove key possession, and confirm Active status with the platform before any operational action, and MUST admit no work until that confirmation and runtime preflight have both succeeded.
- **FR-019**: When protected state or the referenced device key is missing, unreadable, inaccessible to the Manager's process account, fails its integrity check, or disagrees with the platform registration, the Analyst Manager MUST fail closed: admit no work, obtain no credentials, report a bounded diagnostic without protected values, and never recreate, recover, or rebind the previous identity. A lost device key MUST NOT be recoverable or rebindable by any administrator.
- **FR-020**: A Club Admin MUST be able to revoke an Active registration at any time. From the moment revocation is recorded, the platform MUST refuse every Manager request and credential renewal from that registration, independent of whether the Manager is reachable, and revocation MUST take precedence over every local operating state.
- **FR-021**: The Analyst Manager MUST detect revocation through periodic registration-status checks and through authorization refusals on any platform request. On detecting revocation it MUST clear its stamp credentials and cached stamp data, enter Revoked, and then return to Unregistered, and it MUST NOT restore a revoked identity after restart.
- **FR-022**: The host operator MUST be able to unregister the Manager locally. Local unregister MUST stop new work admission, drain, and only after a successful drain request platform unregistration (a cancelled unregister MUST leave the platform registration Active and return the Manager to its previous Running or Paused intent), remove stamp credentials and cached stamp data from protected storage once unregistration is confirmed or a revocation is observed, and return the Manager to Unregistered.
- **FR-023**: Registration state and local operating state MUST be distinct. Running, Paused, Runtime unavailable, and Draining MUST exist only beneath an Active registration, and changing operating state MUST NOT create, activate, revoke, or rebind a registration.
- **FR-024**: The host operator MUST be able to pause, pause for a chosen time, resume, request safe exit, register, request local unregister, and view status from the Manager's tray menu on Windows, macOS, and Linux, or from its status window where the desktop has no tray area. Status MUST report registration state, stamp, club, connectivity, operating state, uptime, and the latest preflight result.
- **FR-025**: Pause MUST stop new work admission immediately while the Manager stays registered, connected, observes revocation, and keeps updating status. Resume MUST return to Running only when the registration is Active and the latest preflight passed.
- **FR-026**: A deliberately paused Manager MUST restart Paused (intent persisted per FR-017; timed pause per FR-036) and MUST NOT resume admission without an explicit resume.
- **FR-027**: Safe exit and local unregister MUST drain: stop new admission immediately and let active Manager-owned work finish until a configurable drain timeout. Safe exit MUST preserve the registration and last operating intent. When the timeout expires, the Manager MUST apply the configured timeout policy, either cancelling the exit or unregister request or stopping and cleaning up the remaining Manager-owned work, and MUST NOT remain in Draining indefinitely.
- **FR-028**: After activation, on every start, and on recovery from runtime loss, the Analyst Manager MUST run a container-runtime preflight before entering Running. Preflight MUST verify that the runtime endpoint is reachable, local, and authorized; the runtime and profile versions are on the supported compatibility list; the required container backend is available; registry authentication and image-platform compatibility succeed; configured processor, memory, disk, and concurrency limits are feasible; and every accelerator to be advertised passes a functional device probe.
- **FR-029**: When preflight fails, the Analyst Manager MUST enter Runtime unavailable, stay registered and connected only for status, diagnostics, and revocation, admit no work, and report each failed check with remediation guidance. It MUST NOT silently select an unvalidated runtime profile.
- **FR-030**: The Analyst Manager MUST report as available only capabilities that passed preflight on the host and MUST NOT infer capabilities from the runtime product name or treat a detected runtime as supported without passing the compatibility checks. While Runtime unavailable it MUST report no execution capability.
- **FR-031**: When a Running or Paused Manager loses runtime access or detects a runtime outside the supported compatibility list, it MUST move to Runtime unavailable and return to its prior Running or Paused intent only after preflight passes again.
- **FR-032**: Runtime integrations MUST be built into the Analyst Manager and selected by configuration; the Manager MUST NOT discover or load arbitrary external runtime extensions, and without a supported runtime integration it MUST remain Runtime unavailable.
- **FR-033**: The platform MUST append an immutable, minimized audit event for each pairing-code issuance and consumption, pending-request creation, authorization denial, approval, rejection, expiry, activation-proof outcome, activation, credential issuance decision, revocation, and unregister. Each event MUST identify the Manager or request, stamp, public-key thumbprint, state transition, acting user or Manager, timestamps, expiry, device metadata, reason where applicable, and correlation identifier.
- **FR-034**: Credentials, proofs, assertions, private keys, pairing codes, polling handles, and broker credentials MUST NOT appear in application logs, audit events, status output, or diagnostics on either the Manager or the platform.
- **FR-035**: All registration, restoration, revocation, operating-control, and preflight behavior MUST be verifiable automatically without an interactive desktop session, an Analyst container, a model, or a production container runtime.
- **FR-036**: A timed pause MUST stop new admission immediately, record its end time with the operating intent, survive restart, end early on explicit resume, and at its end time resume Running automatically only when the registration is Active and the latest preflight passed; otherwise the Running intent is restored and the Manager follows the normal preflight rules. Status MUST show the remaining pause time.
- **FR-037**: The Analyst Manager MUST run as a desktop application in the context of the signed-in user, without an operating-system service and without administrator rights. The host operator MUST be able to enable and disable automatic start at the user's sign-in on Windows, macOS, and Linux; every automatic start follows FR-018.
- **FR-038**: In this slice the device key MUST be a per-user, non-exportable key held in the operating system's key store on Windows (hardware-backed when the host offers it, otherwise software-backed, with the kind reported at registration), in the Keychain (in the Secure Enclave when available) on macOS, and in a PKCS#11 token on Linux. (Refusal behavior per FR-002.)
- **FR-039**: The platform and the Analyst Manager MUST both be verified against the same committed reference examples of every registration exchange (pairing, activation proof, credential request and proof-of-possession, status, revocation responses), so that a format change on either side fails verification.

### Key Entities *(include if feature involves data)*

- **Analyst Manager Registration**: The platform's authoritative, revocable, stamp-local machine identity for one Manager. Holds the stable Manager identity (assigned only at activation), the stamp and its club, the device public-key thumbprint, the lifecycle state (Pairing, Pending approval, Approved, Active, Unregistering, Unregistered, Rejected, Expired, Revoked), the initiating Registrar, the deciding Club Admin, decision reason, timestamps, window expiries, minimized device metadata, and a correlation identifier. Its lifetime is not defined by any short-lived credential.
- **Registration Request**: The key-bound request created when a Registrar submits a valid pairing code; it awaits a Club Admin decision and then device-key activation within bounded windows. It never grants access by itself.
- **Pairing Code and Polling Handle**: High-entropy, single-use, short-lived, rate-limited values bound to the device public key, target stamp, nonce, and expiry. The pairing code is shown to humans; the polling handle lets the waiting Manager learn the request outcome. Neither is ever stored durably by the Manager or logged.
- **Activation Challenge**: A fresh, single-use, request-bound value that the Manager must sign with the paired private key to activate an approved request.
- **Device Identity**: The asymmetric key pair that defines the Manager's machine identity. The private key stays non-exportable inside the host's protected store; the platform knows only the public key and its thumbprint.
- **Manager Credential**: A short-lived, proof-of-possession-bound credential scoped to one Manager identity, one stamp, and Manager-only operations, obtained by proving device-key possession.
- **Protected Local Registration State**: The Manager's protected record of its Manager identity, platform endpoint, stamp binding, device-key reference, and last operating intent; restored on start and removed on unregister or revocation.
- **Operating State**: The local state beneath an Active registration (Running, Paused, Runtime unavailable, Draining), together with the persisted Running or Paused intent, the end time of a timed pause, and the configured drain-timeout policy.
- **Runtime Preflight Result**: The outcome of each runtime check, the active runtime profile and version, endpoint and backend health, validated capabilities, remediation guidance for failures, and when the checks ran.
- **Registration Audit Event**: An immutable, minimized record of one registration lifecycle transition or authorization decision, free of secret values.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A host operator, a Registrar, and a Club Admin can take a newly started, unregistered Analyst Manager to Active in under 5 minutes of combined hands-on time, excluding time spent waiting for the Club Admin to act.
- **SC-002**: In automated validation, 100% of attempts to activate or use a rejected, expired, revoked, unregistered, replayed, wrong-key, or wrong-stamp registration are refused, and 0 of them yield an operational credential.
- **SC-003**: After a Club Admin revokes a registration, 0 Manager requests or credential renewals from that registration are accepted, including while the Manager is disconnected, and a connected Manager returns to Unregistered within one status-check interval.
- **SC-004**: 100% of restarts with intact protected state restore the same Manager identity and stamp binding without any human action, and 100% of deliberately paused Managers come back Paused.
- **SC-005**: 100% of starts with missing, unreadable, inaccessible, or altered protected state result in no admitted work, no issued credential, and a diagnostic visible to the host operator within 30 seconds of start.
- **SC-006**: 100% of invalid, expired, consumed, and unknown pairing-code submissions receive an identical outward response.
- **SC-007**: Inspection of all logs, audit events, status output, diagnostics, and protected local state produced by the full validation suite finds 0 credentials, proofs, private keys, pairing codes, polling handles, or broker credentials.
- **SC-008**: Every registration lifecycle transition and authorization decision exercised in validation produces exactly one attributable audit event (100% coverage, 0 missing actors or correlation identifiers).
- **SC-009**: After a pause is acknowledged, 0 new work admissions occur; in 100% of drain-timeout cases the Manager leaves Draining no later than the configured drain timeout plus the configured cleanup bound.
- **SC-010**: In 100% of failed-preflight cases the Manager is in Runtime unavailable, reports 0 unvalidated capabilities, and shows the host operator the specific failed check with remediation guidance.
- **SC-011**: 100% of acceptance scenarios in this specification can be executed automatically without an interactive desktop session, an Analyst container, a model, or a production container runtime.
- **SC-012**: In 100% of timed-pause tests, admission stops immediately, the pause survives restart, and the Manager resumes no later than 5 seconds after the chosen end time when its registration is Active and preflight passed.

## Assumptions

- **Actors**: The host operator is the local user or IT staff of the machine running the Analyst Manager; this is a local, not a platform, role. Registrar and Club Admin are the platform roles defined by the architecture: Registrar is a club-level role that may initiate Manager registration, and Club Admins inherit it and alone may approve, reject, or revoke.
- **Single-club stamp**: Each deployment stamp represents exactly one club, so the club chosen when submitting a pairing code is always the stamp's club.
- **Window defaults**: The pairing code expires after approximately ten minutes and a consumed request remains available for a Club Admin decision for 24 hours, as stated by the architecture. These and the activation window, rate limits, credential and proof lifetimes, replay windows, status-check interval, backoff bounds, drain timeout, and cleanup bound are configurable with explicit non-production values for validation. Production-approved values remain unresolved and are governed by the production operations profile; no default here implies production approval.
- **Pairing restart**: Pairing codes and polling handles are never persisted, so a Manager restarted mid-pairing abandons that attempt and starts a new one; the abandoned platform request simply expires.
- **Verification address**: The verification address the Manager shows (`/analyst-managers/pair` on the stamp's public origin) is a placeholder in this slice; the browser page arrives with the client applications feature, and until then a Registrar submits the code and fingerprint through the documented API operation.
- **Unreachable platform during unregister**: Local unregister is not considered complete until platform revocation is confirmed or observed; until then the Manager stays non-admitting and retries.
- **Protected storage**: The stores named in FR-038 are the supported candidates for this slice; the per-operating-system evidence needed to approve them for production remains governed by Security and Data Governance. This feature requires fail-closed behavior whenever no supported store is available. Automated validation uses a software PKCS#11 token, which counts as a test-only store that cannot be selected outside validation; the Windows and macOS stores are verified manually on their operating systems.
- **Simulated runtime and work**: Preflight is validated against a simulated container runtime, and drain and safe-exit behavior against simulated Manager-owned work, because this feature acquires and executes no work and ships no production runtime integration.
- **Human-facing operations**: Registrar submission and Club Admin approval, rejection, and revocation are exposed as authorized platform operations reachable from an authenticated browser session; polished production web-client screens for them are deferred to the clients source area.
- **Ownership**: The Analyst Manager belongs to the analysts source area; registration records, human authorization, credential issuance, and audit belong to the platform.
- **Evidence**: Passing validation of this feature is development evidence only and does not establish deployment support or production readiness.
- **Manual evidence before merge**: The Windows and macOS key-store providers and the full pairing-to-revocation run cannot be exercised on the automated Linux verification, so this feature's pull request is always held for a person's review and merges only after the manual Windows, macOS, and end-to-end results are recorded on it.
- **Out of scope**: accessibility certification and visual polish of the tray menu and status window; Analyst containers and their launch, isolation, and cleanup; model execution; production container-runtime integrations; installers, code signing, and updates (automatic start at sign-in is in scope); QR-code rendering; work discovery, queue-depth display, dequeue, attempt claiming, heartbeats, and completion; broker and object-storage credentials; and all unresolved production policy values (credential lifetimes, rate limits, drain timeout, audit authority and retention, keystore mechanisms, installer trust).
- **Deferred to keep this slice focused**: routine device-key rotation with old- and new-key proof; per-operating-system protected-storage evidence spikes; the host-local maximum-concurrency setting; host utilization reporting; advertisement of capabilities to the work queue; and stopping or cleaning up containers on revocation (no containers exist in this slice).
- **Dependencies**: `specs/20261007-115855-environment-verification-coverage` extends automated verification to the Analyst Manager solution and is reviewed and merged before this feature starts. `specs/20261005-130700-platform-persistence-foundation` provides durable platform storage for registration requests, registrations, and audit events. `specs/20261005-130701-club-identity-foundation` provides authenticated human sessions and Registrar and Club Admin authorization. `specs/20261005-130702-recording-lineage-upload` is merged before this feature; it creates the repository-root contracts folder, its index, and the contract tests that this feature's shared reference examples extend. `specs/20261005-130703-durable-analysis-workflow` builds on this feature's Manager authentication and merges after it; this feature consumes none of its work, attempt, or completion behavior.
- **Architecture References**: `docs/architecture/analyst-manager.md`, `docs/architecture/analyst-runtime-and-recovery.md`, `docs/architecture/security-and-data-governance.md`, `docs/architecture/tenancy-and-technology.md`, `docs/architecture/terminology-and-principles.md`, `docs/architecture/analysts-models-and-hardware.md`.
