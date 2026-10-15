# Feature Specification: Analyst Manager Queue Depth and Host Utilization

**Feature Branch**: `20261015-192843-manager-queue-utilization`

**Created**: 2026-10-15

**Status**: Draft

**Input**: User description: "Analyst Manager Queue Depth and Host Utilization in the Status View: host operators decide how much of their machine the Manager may use and need to see whether work is waiting for it and how loaded the host is. Scope: a Manager-authenticated platform operation that returns, for each capability the Manager is currently assigned through its queue credentials, the approximate number of ready jobs waiting in the stamp-local work queue together with the observation time, read from the queue's consumer state, guidance only (never permission to pull or an input to admission); the Manager polls it with its registration check interval and keeps only the latest observation; the Manager samples host CPU and memory utilization on Windows, macOS, and Linux; the tray status window and the CLI status (text and JSON) show queue depth per capability and in total with its observation time and age, plus CPU and memory utilization; when the platform is unreachable the last observation stays visible marked stale with its age; queue depth and utilization stay host-local (not part of the status report or the Web UI fleet, as the architecture keeps them in the Manager). Out of scope: accelerator utilization (no accelerated runtime yet; CUDA deferred), queue depth in the Web UI or the status report, history or trends, alerts and thresholds (production values), using the depth for scheduling, autoscaling, or admission. Depends on Analyst Manager Work Execution and Analyst Manager Status Report and Fleet Read API (both merged). Implements docs/architecture/analyst-manager.md (Tray Profile status window: utilization and queue depth; Fleet Visibility In The Web UI: queue depth remains guidance in the Manager's own status view) and the 'expose local utilization, active attempts, and timestamped queue status' responsibility in docs/architecture/analyst-runtime-and-recovery.md, and contributes the queue-depth measurement asked for by docs/architecture/analysts-models-and-hardware.md (Implementation Validation)."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A host operator sees whether work waits for their Manager (Priority: P1)

A host operator wonders whether to raise the Manager's concurrency limit, keep it paused, or let the machine run overnight. They open the status window or run the status command and see, for each capability their Manager handles, roughly how many ready jobs are waiting on the stamp, the total, and when this was observed. The numbers are clearly labelled as approximate guidance; the Manager keeps pulling work exactly as before.

**Why this priority**: Waiting work is the information a host operator needs to decide how much of the machine to give; without it they tune concurrency and pauses blindly.

**Independent Test**: Can be fully tested by making jobs ready for a capability the Manager is assigned, then reading the status window and the CLI status, which show the waiting count per capability, the total, and the observation time, while admission and pulling behave unchanged.

**Acceptance Scenarios**:

1. **Given** an active Manager assigned to two capabilities and five ready jobs waiting for one of them, **When** the host operator opens the status window or runs the status command after the next poll, **Then** it shows five for that capability, zero for the other, a total of five, and the time of the observation with its age.
2. **Given** jobs that are waiting for a capability the Manager is not assigned, **When** the host operator looks at the status, **Then** those jobs are not counted and that capability is not listed.
3. **Given** a paused or draining Manager, **When** jobs become ready, **Then** the status still shows the waiting count (so the operator can decide to resume), and the Manager still pulls nothing.
4. **Given** the platform becomes unreachable after an observation, **When** the host operator looks at the status, **Then** the last observation stays visible, marked as stale, with its age; after the platform is reachable again, the next poll replaces it.
5. **Given** an active Manager that has not yet received any observation, or that is not assigned any capability, **When** the host operator looks at the status, **Then** the status says that no queue information is available yet, or that the Manager is assigned no capability, instead of showing zero.
6. **Given** the status command with machine-readable output, **When** it runs, **Then** it includes each capability's count, the total, the observation time, and whether the observation is stale.

---

### User Story 2 - A host operator sees how loaded the host is (Priority: P2)

Next to the active Analysts, the host operator sees the host's current CPU utilization and memory use, so they can tell whether the machine has room for more concurrent Analysts or whether the Manager competes with their own work.

**Why this priority**: Utilization complements queue depth for the same decision, but a host operator can also read it from the operating system's own tools.

**Independent Test**: Can be fully tested on Windows, macOS, and Linux by loading the host's CPU and memory and confirming that the status window and the CLI status reflect the change within the sampling period, and that nothing about utilization reaches the platform.

**Acceptance Scenarios**:

1. **Given** a running Manager, **When** the host operator opens the status window or runs the status command, **Then** it shows the host's CPU utilization as a percentage and its memory in use and total, with the time of the sample.
2. **Given** the host's CPU load rises, **When** the next sampling period passes, **Then** the shown CPU utilization rises accordingly.
3. **Given** a host where the Manager cannot read a value, **When** the host operator looks at the status, **Then** that value is shown as not available instead of zero.
4. **Given** any status report the Manager sends to the platform, **When** it is inspected, **Then** it contains no utilization or queue depth values.

---

### Edge Cases

- The Manager's assigned capabilities change (for example after new queue credentials): the next observation lists the new set; capabilities no longer assigned disappear from the status.
- The queue cannot be read by the platform (for example the broker is unreachable): the operation fails with a reason, the Manager keeps the last observation as stale, and pulling is unaffected.
- The Manager is revoked or unregistered: polling stops, and the status no longer shows queue depth.
- Very large counts: the status shows the number as reported, without a cap or rounding that hides the order of magnitude.
- Jobs that are delivered to another Manager but not yet acknowledged: they are not waiting and are not counted (the count is approximate guidance, not an exact backlog).
- The host has many CPUs or a high memory total: utilization is shown for the whole host, not per CPU.
- The host's clock differs from the platform's: the age is computed from the platform's observation time relative to the time the Manager received it, so clock skew does not make an observation look fresh or stale.

## Requirements *(mandatory)*

### Functional Requirements

#### Queue depth

- **FR-001**: The platform MUST offer an operation, authenticated like the Manager's other self operations, that returns for each capability currently assigned to the calling Manager through its queue credentials the approximate number of ready jobs waiting in the stamp-local work queue, the total, and the time of the observation.
- **FR-002**: The operation MUST return only counts for the calling Manager's assigned capabilities and MUST NOT return job identities, match or team information, or the state of other Managers.
- **FR-003**: The operation MUST be refused for Managers that are not active, with the same responses as the Manager's other self operations.
- **FR-004**: When the platform cannot read the work queue, the operation MUST fail with a reason that distinguishes "queue unavailable" from authorization failures.
- **FR-005**: The Manager MUST request queue depth while it is active, with the interval of its registration check, regardless of whether it is running, paused, draining, or runtime-unavailable, and MUST stop when it is no longer active.
- **FR-006**: The Manager MUST keep only the latest observation in memory, MUST NOT persist it, and MUST mark it stale when it is older than two polling intervals or when the latest request failed.
- **FR-007**: Queue depth MUST NOT influence admission, pulling, concurrency, or any other Manager or platform decision; it is shown as guidance only.

#### Host utilization

- **FR-008**: The Manager MUST sample the host's overall CPU utilization (percent of all CPUs) and memory (in use and total) on Windows, macOS, and Linux at a fixed sampling period, and MUST show a value as not available when it cannot read it.
- **FR-009**: The Manager MUST keep only the latest sample in memory and MUST NOT persist samples or keep a history.

#### Display and privacy

- **FR-010**: The tray status window and the CLI status output (text and machine-readable) MUST show the queue depth per assigned capability and the total with the observation time, its age, and a stale marker when stale; and the host's CPU utilization and memory in use and total with the sample time.
- **FR-011**: The status MUST distinguish "no observation yet", "no capability assigned", and a count of zero.
- **FR-012**: The status MUST label queue depth as approximate guidance.
- **FR-013**: Neither queue depth nor utilization MUST be sent in the Manager's status report or any other request to the platform, or appear in the Web UI fleet view.

### Key Entities

- **Queue Depth Observation**: For one Manager at one time: the observation time, and per assigned capability the approximate count of waiting ready jobs; with the total. Transient; held only in the Manager's memory.
- **Host Utilization Sample**: The sample time, overall CPU utilization in percent, and memory in use and total. Transient; held only in the Manager's memory.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After jobs become ready for an assigned capability, the status shows them within one polling interval plus 5 seconds in 100% of automated tests.
- **SC-002**: A host operator can tell from the status alone, without other tools, how many jobs wait for their Manager and how loaded the host is, in under 10 seconds.
- **SC-003**: Within two polling intervals of the platform becoming unreachable, the status marks the queue depth as stale with its age in 100% of automated tests.
- **SC-004**: In automated tests, 0 status reports or other platform requests contain queue depth or utilization values.
- **SC-005**: Admission and pulling decisions are identical with and without queue depth information in 100% of automated tests.
- **SC-006**: A sustained change of host CPU load is reflected in the shown utilization within two sampling periods on Windows, macOS, and Linux.

## Assumptions

- **Dependencies**: Analyst Manager Work Execution (queue credentials, assigned capabilities, admission, the status window and CLI status) and Analyst Manager Status Report and Fleet Read API (registration check interval, status report), both implemented.
- **Tenancy**: A stamp hosts exactly one club, so the stamp-local queue holds only that club's work; the counts reveal no other club's activity.
- **Counting**: "Waiting" means ready jobs not yet delivered to any Manager; jobs being executed or delivered but not yet acknowledged are not counted. The count is approximate and may lag the queue by the platform's read and the polling interval.
- **Sampling**: Utilization is sampled every few seconds (the plan fixes the period) and averaged over that period; it is not a production-governed value.
- **Profiles**: The CLI status covers every profile that exists when this feature is implemented (the tray profile, and the headless server profile once its feature has merged).
- **Out of scope**: accelerator utilization; queue depth or utilization in the status report, the fleet, or the Web UI; history, trends, alerts, and thresholds; any use of the depth for scheduling, autoscaling, or admission; production values for intervals.
- **Architecture**: This feature implements the status-window utilization and queue depth of `docs/architecture/analyst-manager.md` (Tray Profile; Fleet Visibility In The Web UI) and the "expose local utilization, active attempts, and timestamped queue status" responsibility in `docs/architecture/analyst-runtime-and-recovery.md`, and contributes the queue-depth measurement of `docs/architecture/analysts-models-and-hardware.md` (Implementation Validation), correcting any statement that they do not exist.
