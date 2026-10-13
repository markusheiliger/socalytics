# Feature Specification: Analyst Manager Status Report and Fleet Read API

**Feature Branch**: `20261012-153308-manager-fleet-status`

**Created**: 2026-10-12

**Status**: Draft

**Input**: User description: "Analyst Manager Status Report and Fleet Read API, the platform half of the Web UI fleet view. With its existing periodic registration-status check, every active Analyst Manager sends a small status report: operating state (running, paused with its resume time, runtime unavailable with its reason, draining), active and maximum Analysts (the effective concurrency limit from Analyst Manager Work Execution), and its version. The platform keeps only the latest report per Manager and overwrites it each time, with no history. A Manager counts as offline after a configurable number of missed reports derived from the reporting interval the platform tells the Manager; only explicit non-production values exist. Fleet read API: Club Admins see every Manager of their club with host label, registration state, claimed key protection, connectivity (online or offline with the time of the last report), the latest reported operating state, active and maximum Analysts, and version; Registrars see only the registration requests they submitted; the view's only actions are the existing approve, reject, and revoke. The host label is entered by the host operator at pairing (optional, prompted by the CLI's register command), shown to the Registrar in the submission confirmation and in the fleet view; the machine's host name is never sent. Out of scope: the Web UI fleet screen, remote pause or other remote control, queue depth, host utilization reporting, and production values for interval and threshold."

## Clarifications

### Session 2026-10-12

- Q: What should happen to a Manager's latest status report when its registration ends? → A: It is deleted in the same step that ends the registration (revocation or completed unregistration); the audit trail keeps the registration's end. (Recommended option, chosen while the user was unavailable.)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Club Admins see the state of every Analyst Manager (Priority: P1)

A Club Admin wants to know which of the club's Analyst Managers are working, paused, broken, or gone. Each active Manager already checks its registration with the stamp at a regular interval; with that check it now also reports its operating state, how many Analysts it runs out of how many it may run, and its version. The Club Admin reads one list of all the club's Managers with this information and sees at a glance which ones are offline because they stopped reporting.

**Why this priority**: Club Admins approve and revoke Managers but today cannot see whether an approved Manager is doing anything. The fleet list is the information the Web UI fleet view will show.

**Independent Test**: Register two Managers, let one report Running with one active Analyst and the other report Paused with a resume time, stop the second Manager, and read the fleet list as a Club Admin before and after the offline threshold passes.

**Acceptance Scenarios**:

1. **Given** an active Manager, **When** it performs its periodic registration check, **Then** the check carries a status report with its operating state, the resume time when paused, the reason when its runtime is unavailable, its active and maximum Analysts, and its version, and the platform stores it as that Manager's latest report.
2. **Given** a stored report, **When** the same Manager sends a newer one, **Then** the newer report replaces it and no earlier report is kept.
3. **Given** Managers in every registration state, **When** a Club Admin reads the fleet list, **Then** each Manager appears with its host label, registration state, claimed key protection, connectivity, latest reported operating state, active and maximum Analysts, version, and the time of its last report; Managers that never reported show no operating state and are offline.
4. **Given** an active Manager whose last report is older than the configured number of missed reporting intervals, **When** a Club Admin reads the fleet list, **Then** that Manager is shown as offline with the time of its last report.
5. **Given** a Manager that becomes revoked or unregistered, **When** a Club Admin reads the fleet list, **Then** it is shown with its final registration state and no operating state, because its latest report was deleted when the registration ended.
6. **Given** the fleet list, **When** a Club Admin approves, rejects, or revokes a Manager from it, **Then** the existing approval, rejection, and revocation operations apply unchanged.
7. **Given** a member who is neither Club Admin nor Registrar, **When** they read the fleet list, **Then** the request is refused.

---

### User Story 2 - Registrars follow the requests they submitted (Priority: P2)

A Registrar who submitted a pairing code for a new Manager wants to follow what happened to it without seeing the club's whole fleet. They read the requests they submitted with their state (pending, approved, active, rejected, expired, revoked) and host label, and nothing about Managers they did not submit.

**Why this priority**: The architecture limits Registrars to their own requests; today they cannot read any request, so they cannot tell whether a Club Admin acted.

**Independent Test**: As two Registrars, each submit one pairing code; each reads their own requests and sees only theirs; a Club Admin approves one, and its Registrar sees the change.

**Acceptance Scenarios**:

1. **Given** a Registrar who submitted requests, **When** they read their requests, **Then** they see exactly those requests with state, host label, device fingerprint, submission time, and decision time, and no operating-state or connectivity information.
2. **Given** a Registrar, **When** they read a request another member submitted, **Then** the request is refused without revealing whether it exists.
3. **Given** a Club Admin who is also a Registrar, **When** they read the fleet list, **Then** they see the whole fleet as a Club Admin.

---

### User Story 3 - Host operators name their Manager (Priority: P3)

When a host operator registers a Manager, they may give it a host label such as "Video PC clubhouse". The Registrar sees that label when submitting the pairing code, and Club Admins see it in the fleet list. The Manager never sends the machine's own host name.

**Why this priority**: Without a label, Club Admins can only tell Managers apart by fingerprints. The label exists as an optional pairing field; this story makes it reachable and visible.

**Independent Test**: Register through the CLI with a label, submit the code as a Registrar and see the label in the confirmation, and read it in the fleet list; register without a label and confirm the machine name appears nowhere.

**Acceptance Scenarios**:

1. **Given** the host operator starts registration through the CLI, **When** they enter a label (or leave it empty), **Then** the pairing request carries that label (or none), never the machine's host name.
2. **Given** a pairing code with a label, **When** a Registrar submits it, **Then** the submission's confirmation shows the label together with the device fingerprint.
3. **Given** a label longer than the allowed length or with control characters, **When** pairing starts, **Then** the Manager refuses it locally with a clear message.

### Edge Cases

- A Manager reports with a clock far from the platform's: the platform records its own receipt time as the report time; offline is decided only from platform time.
- A report arrives for a Manager whose registration is no longer active: the registration check is refused as today, and the report is not stored.
- A report with unknown operating-state values or impossible numbers (active above maximum, negative values): the report is refused and the previous report stays; the registration check still answers.
- The reporting interval is changed on the platform: Managers learn the new interval with their next check; offline is computed with the interval in force when the report was received.
- A Manager that was offline reports again: it is shown online immediately.
- Many Managers in one club: the fleet list pages like the existing registration list.

## Requirements *(mandatory)*

### Functional Requirements

#### Status report

- **FR-001**: Every registration-status check of an active Analyst Manager MUST carry a status report with its operating state (running, paused, runtime unavailable, draining), the resume time when paused, the reason when its runtime is unavailable, its active and maximum Analysts, and its version.
- **FR-002**: The platform MUST keep only the latest valid report per Manager, overwrite it with every newer report, record its own receipt time, and keep no history of reports.
- **FR-003**: The platform MUST refuse reports with unknown values or impossible numbers without changing the stored report, and MUST still answer the registration check.
- **FR-004**: The platform MUST tell each Manager the reporting interval with every registration check, and the Manager MUST use it for its next check.

#### Connectivity

- **FR-005**: A Manager MUST count as offline when the platform received no report for a configurable number of reporting intervals; the interval and the number MUST be explicit configuration with non-production values.

#### Fleet read

- **FR-006**: Club Admins MUST be able to read a paged list of all of the club's Managers with host label, registration state, claimed key protection, connectivity with the time of the last report, the latest reported operating state with its resume time or reason, active and maximum Analysts, and version, and MUST be able to read one Manager's entry.
- **FR-007**: Registrars without the Club Admin role MUST be able to read only the registration requests they submitted, with state, host label, device fingerprint, submission time, and decision time, and MUST NOT see operating state or connectivity; reading any other request MUST be refused without revealing its existence.
- **FR-008**: Members with neither role MUST be refused.
- **FR-009**: The fleet read MUST NOT add actions; approval, rejection, and revocation stay the existing operations.

#### Host label

- **FR-010**: The Manager's CLI registration MUST let the host operator enter an optional host label; the Manager MUST refuse labels that exceed the platform's allowed length or contain control characters, and MUST NEVER send the machine's host name.
- **FR-011**: The Registrar's pairing-code submission result MUST show the label together with the device fingerprint, and the fleet list MUST show it.

#### Data minimization

- **FR-012**: The platform MUST NOT keep report history, host utilization, or user activity data; the latest report MUST be deleted in the same step that ends the registration (revocation or completed unregistration).

### Key Entities

- **Status report**: The latest operating state, resume time or runtime-unavailable reason, active and maximum Analysts, and version of one Manager, with the platform's receipt time; one per Manager.
- **Fleet entry**: The read model combining a registration, its host label and claimed key protection, and its latest report with the derived connectivity.
- **Reporting settings**: The reporting interval and the number of missed intervals after which a Manager is offline.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In 100% of tests, a Club Admin reading the fleet list sees each active Manager's operating state as of its last report, at most one reporting interval old while the Manager is online.
- **SC-002**: In 100% of tests, a Manager that stopped reporting is shown offline no later than one reporting interval after the configured number of missed intervals.
- **SC-003**: 0 earlier reports remain stored after a newer report from the same Manager in the tests.
- **SC-004**: In 100% of tests, Registrars see exactly the requests they submitted and nothing else; other members see nothing.
- **SC-005**: 0 machine host names are sent by the Manager in the tests.

## Assumptions

- **Report transport**: The report travels with the existing registration-status check (the Manager's self-registration read), so no new connection or schedule is introduced; the plan defines how it is carried.
- **Active and maximum Analysts**: The report always carries both values. Until Analyst Manager Work Execution (the attempt registry and the effective concurrency limit) is merged, the Manager reports the active count of its work tracker and the fixed concurrency limit of 1 (never below the active count). Reporting the registered attempts and the effective limit is deferred to that feature, on the decision of the person who started the diagnosis of the stopped implementation ("Ship without T011; the work-execution spec follows later"); the report contract does not change.
- **Host label**: The optional label already exists in the pairing contract; this feature makes the CLI ask for it and shows it, and changes neither its storage nor its length limit.
- **Fleet list for Club Admins**: It extends the existing registration list rather than adding a parallel list, so Club Admins have one place to read and act.
- **Non-production values**: The reporting interval (default 60 seconds today) and the missed-interval threshold (for example three) are explicit configuration; production values remain unresolved under the production operations architecture.
- **Out of scope**: the Web UI fleet screen (a later Web UI feature); remote pause or any remote control; queue depth; host utilization; production values.
- **Evidence**: Passing validation is development evidence only.
- **Dependencies**: `specs/20261012-141233-manager-work-execution` (Analyst Manager Work Execution) provides active attempts and the effective concurrency limit; it is not a prerequisite, and it takes over reporting them when it merges (see Assumptions). `specs/archive/20261005-130704-analyst-manager-registration` provides the registration check, pairing with the host label, Manager authentication, and the registration list and decisions.
- **Architecture References**: [docs/architecture/analyst-manager.md](../../../docs/architecture/analyst-manager.md) (Fleet Visibility In The Web UI; Browser Pairing And Approval; Machine Authentication), [docs/architecture/security-and-data-governance.md](../../../docs/architecture/security-and-data-governance.md) (data minimization).
