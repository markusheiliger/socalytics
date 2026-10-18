# Feature Specification: Analyst Manager Minimum Version

**Feature Branch**: `20261017-213549-manager-minimum-version`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Analyst Manager Minimum Version: the architecture decides that \"the stamp declares a minimum Manager version. An older Manager admits no new work and shows an update notice until it is updated\", while the minimum version still applies with manual updates (docs/architecture/analyst-manager.md#updates); nothing of it is implemented, and installers, the stamp-served update feed, channels, and pinning are planned only after a Velopack spike. This feature implements only the minimum version: the stamp's deployment configuration declares a minimum Analyst Manager version (validated at start; absent means no minimum); every Manager learns the minimum from the platform through its existing authenticated exchanges (the plan picks the existing registration check, status report, or queue-credential exchange, or a header on Manager API responses, analogous to the Web UI's minimum version) and compares it with its own build version; an older Manager keeps its registration and keeps reporting status, but admits no new work (it stops claiming, lets current attempts finish, and requests no new queue credentials if the plan finds that necessary), and shows an update notice with both versions in the tray status window and the headless CLI status; the platform independently refuses work-acquisition operations (queue credentials, claims, or both, as the plan decides) from Managers whose reported version is below the minimum, with a specific problem response, so correctness never depends on the Manager; the Club Admin fleet read and the Web UI fleet view show which Managers are below the minimum. Changing the minimum takes effect without re-registration; raising it never interrupts running attempts. Out of scope: the update feed, downloads, installers, channels, pinning, signing, attestation verification, automatic or manual self-update, maximum versions, production values for the minimum. Depends on Analyst Manager Registration, Analyst Manager Work Execution, Analyst Manager Status Report and Fleet Read API, Web UI Analyst Manager Fleet and Registration Approval, Analyst Manager Headless Server Profile, Analyst Manager Queue Depth and Host Utilization (merged; archived under specs/archive/). Implements docs/architecture/analyst-manager.md#updates (minimum version only) and the fleet-view parts of docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui."

## Clarifications

### Session 2026-10-17

- Q: Should pending registration requests (not yet approved) also be marked as below the minimum in the Club Admin fleet? → A: Yes; every current registration, including pending requests, is marked from its effective version, so a Club Admin sees it before approving; Registrar views stay unchanged.
- Q: Where should a tray Manager show the update notice besides its status window? → A: Also as a status line in the tray menu; no operating-system notification.
- Q: Should version refusals be observable beyond structured logs? → A: Yes; a platform counter of refusals by operation through the existing telemetry, without registration identifiers as metric dimensions and without a security audit event.
- Q: Must a changed minimum take effect while the platform keeps running, or is a platform restart acceptable? → A: While running; the platform reads the current configured value for every exchange and refusal, so a configuration reload applies it without restart, and a restart applies it as well.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - An outdated Manager stops taking new work and says why (Priority: P1)

A stamp operator raises the stamp's minimum Analyst Manager version because older Managers can no longer process
current work correctly. A host operator's Manager is older than that minimum. Without being re-registered, the Manager
learns the minimum at its next registration check, stops taking new work, lets the Analysts it is already running
finish normally, and shows an update notice in its tray status window or in the headless CLI status that names its own
version and the required minimum. It stays registered and keeps reporting its status. Once the operator installs a
newer Manager (outside this feature) or the minimum is lowered again, the Manager takes new work again without any
other action.

**Why this priority**: This is the architecture's rule itself (an older Manager admits no new work and shows an update
notice). It protects analysis results from outdated Managers and tells the host operator what to do.

**Independent Test**: Run a registered Manager of a known version against a platform whose minimum is above it; the
Manager claims no job, its running attempt completes, its status shows the update notice with both versions, and after
the minimum is lowered it claims work again, all without re-registration.

**Acceptance Scenarios**:

1. **Given** an active Manager whose version is below the stamp's minimum, **When** it completes its next registration
   check, **Then** it admits no new work, its local status shows an update notice with its own version and the
   minimum, and its registration and status reports continue unchanged.
2. **Given** a Manager running an Analyst when the minimum is raised above its version, **When** it learns the new
   minimum, **Then** the running attempt continues with heartbeats, result uploads, and its completion callback, and
   only new work is refused.
3. **Given** a Manager below the minimum, **When** the minimum is lowered to or below its version or removed, **Then**
   at its next registration check the update notice disappears and it admits new work again, subject to its
   operating state and concurrency limit.
4. **Given** a Manager whose version equals the minimum, **When** it checks its registration, **Then** it admits work
   as before and shows no update notice.
5. **Given** a headless Manager below the minimum, **When** its operator runs the CLI `status` command (plain and
   `--json`), **Then** the output names the update notice, the Manager's version, and the minimum.

---

### User Story 2 - The platform refuses work to outdated Managers on its own (Priority: P1)

The platform does not rely on the Manager to obey the minimum. Whenever a Manager whose version is below the stamp's
minimum asks for queue access or tries to claim a job, the platform refuses with a specific problem response that
names the reason, so no outdated Manager can start new work, even a modified one or one that has not yet learned the
minimum. Operations for work it already holds (heartbeats, result grants, upstream reads, and completion) are not
refused because of the version.

**Why this priority**: Correctness must never depend on the Manager (constitution and architecture rule that the
stamp validates every work operation). It is equally critical to User Story 1.

**Independent Test**: With a minimum above a Manager's reported version, request queue credentials and claim a ready
job as that Manager through the API; both are refused with the version problem, while a heartbeat and the completion
of an attempt it claimed before the change succeed.

**Acceptance Scenarios**:

1. **Given** a Manager whose reported version is below the minimum, **When** it requests queue credentials, **Then**
   the platform refuses with the version problem response and issues no credential.
2. **Given** a Manager whose reported version is below the minimum, **When** it tries to claim a ready job, **Then**
   the platform refuses with the version problem response, creates no attempt, and the job stays available to other
   Managers.
3. **Given** a Manager that claimed a job before the minimum was raised, **When** it renews the lease, requests result
   grants, reads upstream results, and completes the attempt, **Then** each operation behaves as it does without a
   minimum.
4. **Given** no minimum is configured, **When** any Manager requests queue credentials or claims a job, **Then**
   behavior is unchanged from today.
5. **Given** a Manager receives the version problem response on a claim, **When** it handles the refusal, **Then** it
   returns the job to the queue for other Managers and stops admitting new work as in User Story 1.

---

### User Story 3 - Club Admins see which Managers need an update (Priority: P2)

A Club Admin opens the Analyst Manager fleet view in the Web UI. Next to each Manager's version, the view marks every
Manager whose version is below the stamp's minimum as needing an update and names the minimum, so the Club Admin can
ask the host operators to update. The fleet read API carries the same information for every Manager it returns to a
Club Admin.

**Why this priority**: Visibility helps operators act, but the platform and the Managers already enforce the rule
without it.

**Independent Test**: With two active Managers, one below and one at the minimum, read the fleet as a Club Admin
through the API and in the Web UI; only the outdated Manager is marked, with its version and the minimum.

**Acceptance Scenarios**:

1. **Given** a minimum and a Manager whose version is below it, **When** a Club Admin reads the fleet list or the
   registration detail, **Then** the entry states that the Manager is below the minimum and names the minimum.
2. **Given** no minimum is configured, **When** a Club Admin reads the fleet, **Then** no Manager is marked.
3. **Given** a Manager below the minimum, **When** a Club Admin opens the Web UI fleet view or that Manager's detail,
   **Then** the Manager is visibly marked as needing an update with its version and the minimum, readable by screen
   readers and not conveyed by color alone.
4. **Given** a Registrar reads their own submitted requests, **When** the response is returned, **Then** it carries
   no fleet operations data, as today.
5. **Given** a pending registration request from a Manager whose claimed version is below the minimum, **When** a
   Club Admin reviews it, **Then** the request is marked as below the minimum before the Club Admin approves it.

---

### Edge Cases

- A Manager that has not sent a status report yet: the platform compares the version the Manager claimed at pairing;
  once a status report exists, the latest reported version applies.
- A Manager's reported version is not a well-formed version: when a minimum is configured, the platform treats it as
  below the minimum; without a minimum nothing changes.
- The minimum is raised while the Manager is unreachable: the platform refuses its work acquisition at once; the
  Manager shows the notice after its next successful registration check.
- A Manager receives a minimum value it cannot parse: it treats itself as below the minimum (fail closed) and shows
  the raw value in the notice.
- A paused, draining, or runtime-unavailable Manager below the minimum keeps its operating state; the update notice
  is shown in addition to it, and resuming does not admit work while the Manager is below the minimum.
- A Manager below the minimum may still be paired, approved, and activated; it admits no work until it is updated or
  the minimum is lowered.
- Unregistration and revocation of a Manager below the minimum behave as today.
- A queue credential issued before the minimum was raised stays valid until it expires; the platform's claim refusal
  still prevents new attempts.

## Requirements *(mandatory)*

### Functional Requirements

#### Minimum version configuration

- **FR-001**: The stamp's deployment configuration MUST allow declaring a minimum Analyst Manager version; an absent
  or empty value MUST mean that no minimum applies.
- **FR-002**: The platform MUST validate a configured minimum when it starts and MUST refuse to start when the value
  is not a well-formed version of the form the Analyst Manager reports.
- **FR-003**: A changed minimum MUST take effect for every registered Manager without re-registration, pairing, or a
  change to any registration, and without restarting the platform: the platform MUST use the currently configured
  value for every exchange and refusal, so a configuration reload applies it; a reloaded value MUST pass the same
  validation as FR-002, and an invalid reloaded value MUST leave the last valid value in effect and be logged.

#### Learning and applying the minimum in the Manager

- **FR-004**: The platform MUST tell every active Manager the current minimum (or that none applies) through an
  existing authenticated Manager exchange that the Manager performs at least once per registration-check interval.
- **FR-005**: The Manager MUST compare the minimum with its own build version, the same version it reports in its
  status report, and MUST consider itself below the minimum when its version is lower or the minimum cannot be
  parsed.
- **FR-006**: While below the minimum, the Manager MUST admit no new work: it MUST claim no job, MUST return any
  delivered but unclaimed job to the queue for other Managers, and MUST request no new queue credentials.
- **FR-007**: While below the minimum, the Manager MUST keep its registration, keep sending its registration checks
  and status reports, and let every running attempt continue with heartbeats, result uploads, and its completion
  callback until it ends.
- **FR-008**: While below the minimum, the Manager MUST show an update notice that names its own version and the
  minimum in the tray status window, as a status line in the tray menu, and in the local-control CLI `status` output
  (plain text and `--json`), in addition to its operating state; it MUST NOT raise an operating-system notification.
- **FR-009**: When the Manager is no longer below the minimum (the minimum was lowered or removed), it MUST remove
  the notice and admit new work again at its next successful registration check, subject to its operating state,
  concurrency limit, and capability matching.
- **FR-010**: When a claim or queue-credential request is refused with the version problem response, the Manager MUST
  treat itself as below the minimum until its next successful registration check says otherwise.

#### Platform enforcement

- **FR-011**: The platform MUST refuse queue-credential issuance and new job claims from a Manager whose version is
  below the configured minimum with a specific problem response that names the reason and the minimum, and MUST NOT
  issue a credential or create an attempt for such a request; an idempotent repeat of a claim granted before the
  minimum applied returns its existing grant (FR-014).
- **FR-012**: The platform MUST determine a Manager's version from its latest accepted status report, or from the
  version claimed at pairing while no status report exists, and MUST treat a version that is not well formed as below
  a configured minimum.
- **FR-013**: The platform MUST NOT refuse lease renewals, result grants, upstream reads, completions, registration
  checks, status reports, queue-depth reads, unregistration, or any human-facing operation because of the Manager's
  version.
- **FR-014**: Raising the minimum MUST NOT fence, cancel, or fail any running attempt.
- **FR-015**: Without a configured minimum, every Manager and platform operation MUST behave exactly as before this
  feature.

#### Fleet visibility

- **FR-016**: The Club Admin fleet list and registration detail MUST state, for every current registration including
  pending requests, whether the Manager's version (as determined by FR-012) is below the configured minimum, and MUST
  name the minimum when one is configured; Registrar views MUST remain without fleet operations data.
- **FR-017**: The Web UI fleet view and registration detail MUST mark every Manager below the minimum as needing an
  update, with its version and the minimum, through text that assistive technologies announce and not through color
  alone.

#### Observability and documentation

- **FR-018**: Every refusal for the minimum version MUST be recorded in structured platform logs with the
  registration, the Manager's version, the minimum, and the refused operation, without credentials, tokens, or
  proofs, and MUST be counted in a platform metric by refused operation, without registration identifiers as metric
  dimensions; refusals are not security audit events.
- **FR-019**: The platform and Analyst Manager documentation MUST describe the minimum-version setting, its default,
  the Manager's behavior below it, the platform's refusals, and that the update feed, installers, and self-update do
  not exist yet.

### Key Entities

- **Minimum Analyst Manager Version**: The stamp's deployment setting naming the oldest Manager version that may
  acquire new work; absent means no minimum. Not stored per registration.
- **Effective Manager Version**: The version the platform attributes to a registration: the latest accepted status
  report's version, otherwise the version claimed at pairing.
- **Update Notice**: The Manager's local indication, alongside its operating state, that it is below the minimum,
  with its own version and the minimum.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests, a Manager below the minimum claims 0 jobs and receives 0 queue credentials, while a
  Manager at or above the minimum processes work unchanged.
- **SC-002**: In automated tests, 100% of attempts running when the minimum is raised complete with their normal
  outcome.
- **SC-003**: A changed minimum is reflected in a Manager's admission and update notice within one registration-check
  interval plus 10 seconds, without re-registration, in 100% of automated tests.
- **SC-004**: In automated tests, direct API requests for queue credentials and claims from a Manager below the
  minimum are refused with the version problem response in 100% of cases, independent of the Manager's own behavior.
- **SC-005**: A Club Admin can identify every Manager below the minimum from the fleet view alone, with 0 Managers at
  or above the minimum marked, in automated browser tests.
- **SC-006**: With no minimum configured, the existing platform, Manager, and Web UI test suites pass unchanged.

## Assumptions

- **Dependencies**: Analyst Manager Registration, Analyst Manager Work Execution, Analyst Manager Status Report and
  Fleet Read API, Web UI Analyst Manager Fleet and Registration Approval, Analyst Manager Headless Server Profile, and
  Analyst Manager Queue Depth and Host Utilization are merged (archived under `specs/archive/`). No spec of the current
  batch is required.
- **Versions**: The Analyst Manager reports a `MAJOR.MINOR.PATCH` build version today; the minimum uses the same form
  and is compared numerically per part. Prerelease or build suffixes are not part of this feature.
- **Configuration**: The minimum is deployment configuration with an empty default (no minimum); only explicit
  non-production values are used in Development and Test, and production values remain open as the architecture
  requires.
- **Timing**: Managers learn the minimum at their existing registration-check interval; the platform applies a
  changed minimum as soon as its configuration is reloaded or the platform restarts.
- **Out of scope**: the stamp-served update feed, downloads, installers, release channels, pinning, signing,
  attestation verification, automatic or manual self-update, maximum versions, and production values for the
  minimum.
- **Architecture**: Implements the minimum-version rule of
  [Updates](../../docs/architecture/analyst-manager.md#updates) and the version parts of
  [Fleet Visibility In The Web UI](../../docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui); the
  rest of Updates stays unimplemented.
