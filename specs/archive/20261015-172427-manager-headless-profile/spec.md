# Feature Specification: Analyst Manager Headless Server Profile

**Feature Branch**: `20261015-172427-manager-headless-profile`

**Created**: 2026-10-15

**Status**: Draft

**Input**: User description: "Analyst Manager Headless Server Profile (Linux): GPU servers without a desktop must run the Analyst Manager. The Core worker runs without Avalonia as a systemd user service with lingering, so it runs without an open session, controlled only by the existing local-control CLI. Scope: a headless entry point that hosts the shared Core worker without UI dependencies; a documented, enabled systemd user unit that replaces desktop autostart; a lingering check at start that warns clearly when lingering is off; the account rule (with a rootful Docker daemon reached through the docker group, the Manager must run under a dedicated non-login service account; the Manager detects it and warns); registration, pause, timed pause, resume, safe exit, unregister, and status through the CLI, with register asking for the host label and showing the verification URL, pairing code, and device fingerprint; Managers claim their application profile (tray or headless-server) at pairing, the platform records it and shows it as claimed in the approval view and in the Web UI fleet, with a warning; a stamp may allow software-backed device keys only for Managers claiming the headless server profile (tray Managers keep requiring hardware-backed keys outside development and test); Linux only. Out of scope: installers, AppImage packaging, and updates (Velopack spike); RHEL-family distributions; hardware key attestation; the real container runtime (Docker Desktop Runtime Adapter spec; tests use the simulated runtime); production values. Depends on Analyst Manager Registration, Analyst Manager Work Execution, Analyst Manager Status Report and Fleet Read API, and Web UI Analyst Manager Fleet and Registration Approval. Implements docs/architecture/analyst-manager.md (Application Profiles, Headless Server Profile, Browser Pairing And Approval for the software-backed key exception and claimed profile, the profile part of Fleet Visibility In The Web UI), docs/architecture/analyst-runtime-and-recovery.md (account rule), and the headless validation on Ubuntu Server 22.04 and 24.04 LTS in docs/architecture/analysts-models-and-hardware.md (Implementation Validation)."

## Clarifications

### Session 2026-10-15

- Q: When a headless Manager runs under a login-capable account and its runtime is a rootful daemon reached through group membership, should it refuse to run Analysts or only warn? → A: Refuse work: it reports its runtime as unavailable with the account reason, stays registered, and remains visible; it does not refuse to start.
- Q: When a Manager built before this feature pairs without claiming an application profile, should the platform refuse the pairing or accept it? → A: Accept it: the profile is recorded as not reported and treated like `tray` for the software-backed key rule; Managers of this feature always send the claim, so the pairing contract stays compatible.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A host operator runs the Manager on a Linux server without a desktop (Priority: P1)

A host operator administers a GPU server that has no desktop and on which nobody is signed in. After a one-time administrator step that enables lingering for the account, they enable the Manager as a user service. It starts at boot, keeps running without a login session, and behaves like the tray Manager without any window or tray: it checks its registration, reports its status, and pulls and runs work while it is Running. The operator controls it only through the existing local-control commands.

**Why this priority**: Servers without a desktop are the main execution hosts for analysis. Without this profile, a Manager only runs while someone stays signed in to a desktop.

**Independent Test**: Can be fully tested on an Ubuntu Server host (or an equivalent CI image) without a graphical environment: enable lingering and the user service, reboot or end all sessions, and confirm that the Manager runs, answers status, pause, resume, and safe exit through the CLI, and executes simulated work while Running.

**Acceptance Scenarios**:

1. **Given** a Linux host without a graphical environment, **When** the host operator starts the headless Manager, **Then** it starts the same worker as the tray profile without loading any graphical component, and the local-control commands work as in the tray profile.
2. **Given** the documented user service is enabled and lingering is on, **When** the host restarts and nobody signs in, **Then** the Manager starts and keeps running, and stops cleanly when the service is stopped.
3. **Given** lingering is off for the account, **When** the headless Manager starts, **Then** it shows and logs a clear warning that it stops with the last session, naming the administrator step that enables lingering; it keeps running in the current session.
4. **Given** an active, Running headless Manager, **When** the operator runs pause, timed pause, resume, safe exit, concurrency, and status, **Then** each behaves exactly as in the tray profile, including that active attempts finish after a pause.
5. **Given** a headless Manager, **When** the operator asks for the graphical status window or tray actions, **Then** none exists; the CLI status output is the complete status view.
6. **Given** a Windows or macOS host, **When** someone tries to start the headless profile, **Then** it refuses to start and names the tray profile as the supported profile on that system.

---

### User Story 2 - A host operator registers a headless Manager from the command line (Priority: P2)

Without a browser on the server, the host operator registers the Manager with the CLI. The register command asks for the host label, then shows the verification URL, the pairing code, and the device fingerprint, so that a Registrar can complete pairing in the Web UI on another device. The Manager claims the headless server profile at pairing. The Registrar and the Club Admin see the claimed profile, marked as claimed and not verified, next to the claimed key protection.

**Why this priority**: A headless Manager can do nothing before it is registered, and the claimed profile is what decides whether a software-backed key is acceptable.

**Independent Test**: Can be fully tested by registering a headless Manager through the CLI against a stamp, completing pairing in the Web UI on another machine, approving it, and confirming that the Manager activates and that the approval view and the fleet show the headless server profile as claimed, with the warning.

**Acceptance Scenarios**:

1. **Given** an unregistered headless Manager, **When** the operator runs the register command, **Then** it asks for the host label and shows the verification URL, the pairing code, its expiry, and the device fingerprint as text, and then waits for approval and activation as in the tray profile.
2. **Given** a pairing request from a headless Manager, **When** a Registrar submits its code and fingerprint, **Then** the platform records the claimed application profile `headless-server` with the request, and the submission confirmation shows it.
3. **Given** a pending request, **When** a Club Admin opens it to decide, **Then** the decision view shows the claimed profile and the claimed key protection, both labelled as claimed by the Manager and not verified, with the warning to approve only Managers the approver can physically identify.
4. **Given** an active headless Manager, **When** a Club Admin opens the fleet, **Then** its entry shows the claimed profile `headless-server`, and tray Managers show `tray`.
5. **Given** a Manager registered before this feature (which claimed no profile), **When** a Club Admin opens the fleet, **Then** its profile shows as not reported, and nothing else about it changes.

---

### User Story 3 - A stamp allows software-backed keys only for headless servers without a TPM (Priority: P3)

Some servers have neither a TPM nor a virtual TPM, so they cannot hold a hardware-backed device key. The stamp operator can allow software-backed device keys only for Managers that claim the headless server profile. Tray Managers keep requiring hardware-backed keys outside development and test. Approvers see the software-backed key and the claimed profile with a warning.

**Why this priority**: It unlocks servers without a TPM, but most target servers have a TPM or virtual TPM, so the profile itself delivers value without it.

**Independent Test**: Can be fully tested by configuring a stamp outside the development and test environments with the headless-only allowance, then pairing a headless Manager with a software-backed key (accepted) and a tray Manager with a software-backed key (refused), and a headless Manager on a stamp without the allowance (refused).

**Acceptance Scenarios**:

1. **Given** a stamp outside development and test that allows software-backed keys for headless servers, **When** a Manager claiming `headless-server` pairs with a software-backed key, **Then** the pairing succeeds and the request shows the software-backed key and the claimed profile with the warning.
2. **Given** the same stamp, **When** a Manager claiming `tray` pairs with a software-backed key, **Then** the pairing is refused with a reason the Manager shows, naming hardware-backed key storage as required.
3. **Given** a stamp outside development and test without the allowance, **When** any Manager pairs with a software-backed key, **Then** the pairing is refused as today.
4. **Given** a stamp in development or test that allows software-backed keys, **When** a Manager with either profile pairs with a software-backed key, **Then** the pairing succeeds as today.
5. **Given** a headless Manager, **When** it looks up the stamp before pairing, **Then** the stamp tells it whether a software-backed key would be accepted for its profile, and the Manager refuses to start pairing (with the reason) when its only available key protection would be refused.

---

### User Story 4 - A host operator is stopped from running Analysts under a root-equivalent account (Priority: P4)

On Linux servers the container runtime may be a rootful Docker daemon reached through membership of the `docker` group, which makes the account root-equivalent on the host. The architecture requires a headless Manager to run under a dedicated non-login service account in that case. The Manager detects this and refuses to admit work under a login-capable account, with a clear reason, so that a misconfigured server never runs Analysts under a person's account; it stays registered, keeps reporting its status, and keeps running.

**Why this priority**: It protects the host, but it only applies once a Linux runtime adapter reports a rootful daemon; until then the simulated runtime exercises the rule.

**Independent Test**: Can be fully tested with the simulated runtime configured to report a rootful daemon: under a login-capable account the Manager reports the runtime as unavailable with the account reason and admits no work; under a non-login account, or with a rootless runtime, it admits work.

**Acceptance Scenarios**:

1. **Given** a headless Manager whose runtime preflight reports a rootful daemon reached through group membership, **When** the account has a login shell, **Then** the Manager admits no work, reports its runtime as unavailable with a reason naming the dedicated non-login service account requirement, and the CLI status and the fleet show that reason.
2. **Given** the same runtime and a dedicated account without a login shell, **When** preflight runs, **Then** the account rule passes and work admission follows the usual rules.
3. **Given** a rootless runtime, **When** preflight runs under any account, **Then** the account rule does not apply.

---

### Edge Cases

- The service starts before the network or the stamp is reachable: the Manager retries its registration check with the existing bounded backoff and reports nothing as offline on its own side.
- A second Manager instance starts for the same account (for example the service and a manual start): the existing single-instance hand-off of the local socket applies; the second instance exits after handing over, as in the tray profile.
- Lingering is turned off while the Manager runs: the next start warns; the running instance is not affected until the system ends it with the last session.
- The register command runs in a non-interactive context (no terminal): it fails with a message asking for the host label as an argument instead of waiting for input.
- The operator presses Ctrl+C during register: pairing stops, the pending pairing code is abandoned, and no registration state changes, as when the tray's pairing dialog is cancelled.
- The service is stopped by the system (`SIGTERM`): the Manager performs the same safe exit as the CLI's exit command, waiting for active attempts within the drain bound.
- A Manager claims `headless-server` on a host that is in fact a desktop: the platform cannot verify the claim; the approver sees it as claimed with the warning, and the allowance for software-backed keys applies only to the claim.
- A Manager claims a profile the platform does not know: pairing is refused with a validation reason.
- A Manager built before this feature pairs without a claim: pairing proceeds, the profile shows as not reported, and a software-backed key is accepted only where the stamp accepts it for tray Managers.
- A Manager restored from backup (the existing safe-restore rules) keeps the profile it claimed at pairing; the claim never changes without a new registration.

## Requirements *(mandatory)*

### Functional Requirements

#### Headless operation

- **FR-001**: The Analyst Manager MUST offer a headless server profile on Linux that runs the same Core worker as the tray profile without loading any graphical or tray component, and MUST refuse to start this profile on Windows and macOS with a message naming the tray profile.
- **FR-002**: In the headless profile, registration, pause, timed pause, resume, safe exit, unregister, concurrency, and status MUST be available through the existing local-control CLI with the same behavior, outcomes, and refusal reasons as in the tray profile.
- **FR-003**: The headless CLI status output MUST include everything the tray status window shows today (stamp, club, connectivity, operating state and pause end, active and maximum Analysts, active attempts with their last confirmed heartbeat, registration state, and version) together with the application profile.
- **FR-004**: The Manager MUST ship a documented systemd user unit for the headless profile that starts the Manager when the user manager starts, restarts it after a crash with a bounded delay, and stops it through the same safe exit as the CLI exit command; enabling the unit replaces desktop autostart for this profile.
- **FR-005**: At every start in the headless profile, the Manager MUST check whether lingering is enabled for its account and, when it is not, MUST show and log a warning that names the consequence (the Manager stops with the last session) and the administrator step that enables it; the check MUST NOT stop the Manager.
- **FR-006**: The headless profile MUST NOT require administrator rights or any system-wide service; everything it installs or changes MUST belong to the account that runs it.
- **FR-007**: The register command in the headless profile MUST ask for the host label (or accept it as an argument), and MUST show the verification URL, the pairing code with its expiry, and the device fingerprint as text; in a non-interactive context without a host label argument it MUST fail with a message instead of waiting for input.

#### Claimed application profile

- **FR-008**: Every Manager of this feature MUST claim its application profile, `tray` or `headless-server`, in its pairing request; the platform MUST refuse unknown values with a validation reason and MUST refuse `headless-server` claims whose reported operating system is not Linux. A pairing request without a claim (from a Manager built before this feature) MUST be accepted, with the profile recorded as not reported.
- **FR-009**: The platform MUST record the claimed profile with the registration request and keep it unchanged for the life of the registration; a different profile requires a new registration.
- **FR-010**: The Registrar's submission confirmation, the Club Admin's decision view, and the fleet list and detail MUST show the claimed profile next to the claimed key protection, both labelled as claimed by the Manager and not verified; registrations without a claim (created before this feature, or paired by an older Manager) MUST show the profile as not reported.
- **FR-011**: The platform MUST NOT base any authorization, work assignment, or capability decision on the claimed profile, except the acceptance of software-backed keys in FR-012.

#### Software-backed keys

- **FR-012**: A stamp MUST support three settings for software-backed device keys: refused (the default outside development and test), allowed only for Managers claiming `headless-server`, and allowed for all profiles (permitted only in development and test).
- **FR-013**: The platform MUST refuse a pairing whose claimed key protection is software-backed unless the stamp setting allows it for the claimed profile, treating a missing claim like `tray`, with a refusal reason that the Manager shows and that names hardware-backed key storage as required.
- **FR-014**: Stamp discovery MUST tell the Manager, per application profile, whether software-backed keys are accepted, and the Manager MUST refuse to start pairing, with that reason, when the only key protection it can use would be refused.
- **FR-015**: The platform MUST refuse to start a stamp outside development and test that is configured to allow software-backed keys for all profiles.

#### Account rule

- **FR-016**: In the headless profile, when the runtime preflight reports a rootful container daemon reached through group membership and the Manager's account has a login shell, the Manager MUST treat the runtime as unavailable, admit no work, and report a reason naming the requirement of a dedicated non-login service account; the CLI status and the fleet MUST show that reason.
- **FR-017**: The account rule MUST NOT apply to rootless runtimes, and MUST pass for accounts without a login shell.

#### Audit and documentation

- **FR-018**: The existing pairing audit events MUST include the claimed profile; refusals because of the software-backed key setting MUST be audited with the claimed profile and protection, without key material.
- **FR-019**: The Manager's documentation MUST describe the headless installation for Ubuntu Server 22.04 and 24.04 LTS: the one-time administrator step that enables lingering, the dedicated non-login service account for rootful runtimes, enabling and controlling the user unit, CLI registration, and where logs go.

### Key Entities

- **Application Profile Claim**: The profile a Manager claims at pairing, `tray` or `headless-server`; recorded with the registration request, immutable for the registration, shown as claimed; absent (not reported) for registrations created before this feature or paired by an older Manager.
- **Software Key Setting**: A stamp's policy for software-backed device keys: refused, headless servers only, or all profiles (development and test only).
- **Headless Service Unit**: The documented user unit that starts, restarts, and safely stops the headless Manager for one account.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On Ubuntu Server 22.04 and 24.04 LTS without a graphical environment, an enabled headless Manager with lingering on is running within 60 seconds after boot with nobody signed in, in 100% of the validation runs.
- **SC-002**: Every local-control command gives the same outcome in the headless profile as in the tray profile in 100% of the automated command tests.
- **SC-003**: A host operator registers a headless Manager from the command line, and a Registrar completes pairing in the Web UI on another device, in under 5 minutes plus the approval time.
- **SC-004**: In automated tests, 100% of software-backed key pairings follow the stamp setting and the claimed profile (accepted or refused as FR-012 and FR-013 define), and every refusal names its reason.
- **SC-005**: Club Admins can tell the claimed profile and key protection of every Manager in the fleet at a glance, with registrations from before this feature shown as not reported.
- **SC-006**: Under a login-capable account with a rootful runtime, the headless Manager admits no work in 100% of the automated tests and states the account reason.

## Assumptions

- **Dependencies**: Analyst Manager Registration (pairing, device keys, local-control CLI, single-instance socket, safe restore), Analyst Manager Work Execution (admission, pause, drain, safe exit), Analyst Manager Status Report and Fleet Read API (status report, fleet read), and Web UI Analyst Manager Fleet and Registration Approval (decision and fleet views), all implemented.
- **Runtime**: No Linux container runtime adapter exists yet; the Docker Desktop Runtime Adapter spec is in progress and targets Docker Desktop. This feature is verified with the simulated runtime, which gains the ability to report a rootful or rootless daemon; real rootful and rootless runtimes follow with a Linux runtime adapter.
- **Device keys**: The headless profile uses the same PKCS#11 provider as the Linux tray profile (tpm2-pkcs11 in production, SoftHSM2 in tests); hardware key attestation remains a later decision, and production approval of key stores stays governed by `GOV-CRED-002`.
- **Distribution**: Ubuntu Server 22.04 and 24.04 LTS are the supported distributions; a RHEL-compatible distribution follows later. Installers, AppImage packaging, and updates wait for the Velopack spike; until then the headless Manager is run from a build output as the tray profile is today.
- **Account detection**: A login-capable account is one whose login shell is not a no-login shell; the Manager reads this from the system's account database without administrator rights.
- **Production values**: The restart delay of the unit and the warning texts are not production-governed; the software key setting outside development and test remains subject to `GOV-CRED-002`.
- **Out of scope**: installers, packaging, and updates; RHEL-family distributions; hardware key attestation; real Linux container runtimes; changes to the tray profile beyond the profile claim; production values.
- **Architecture**: This feature implements `docs/architecture/analyst-manager.md` (Application Profiles, Headless Server Profile, the software-backed key exception and the claimed profile in Browser Pairing And Approval, and the profile in Fleet Visibility In The Web UI), the account rule in `docs/architecture/analyst-runtime-and-recovery.md`, and the headless validation in `docs/architecture/analysts-models-and-hardware.md` (Implementation Validation), and corrects their statements that the headless profile and the claimed profile are not implemented.
