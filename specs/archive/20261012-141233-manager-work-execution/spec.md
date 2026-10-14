# Feature Specification: Analyst Manager Work Execution With a Simulated Analyst

**Feature Branch**: `20261012-141233-manager-work-execution`

**Created**: 2026-10-12

**Status**: Draft

**Input**: User description: "Analyst Manager Work Execution With a Simulated Analyst: close the loop between the stamp's analysis workflow and a registered Analyst Manager. An Active Manager in Running pulls only compatible, stamp-local Logical Jobs from the work queue while it is below its effective concurrency limit and not paused, draining, or runtime-unavailable; claims each delivery through the existing platform claim operation and acknowledges it only after a successful or deterministically obsolete claim; executes the attempt through the runtime adapter boundary against a simulated Analyst (no real container, model, or object storage) that can be configured to succeed, fail, hang, or crash; renews the lease with heartbeats (bounded backoff while the lease is valid, never assuming renewal); and reports exactly one idempotent completion per attempt with fencing token, pinned digests, and a result reference. Fenced or stale attempts are abandoned, late heartbeats and completions are treated as obsolete. Pause stops new pulls immediately while active attempts finish; drain and safe exit wait for active attempts; lowering the concurrency limit never cancels running work; administrative revocation fences the Manager's active attempts on the platform and makes the Manager stop and clean up its simulated work. The tray, status window, and CLI show active attempts and their last successful heartbeat. The Manager obtains short-lived, stamp- and subject-scoped queue credentials through its authenticated API instead of storing a durable broker secret. Out of scope: real Docker/Podman runtime adapters, image pull, container isolation and orphan reconciliation; the Analyst SDK and any capability implementation; result upload, manifests in object storage, and manifest-content validation; capability advertisement beyond what queue filtering needs; the Manager status report and fleet view; production values for leases, heartbeats, retries, concurrency, and credential lifetimes."

## Clarifications

### Session 2026-10-12

- Q: Which queue access does the Manager get in this slice? → A: Real per-Manager, short-lived credentials limited to pulling and acknowledging its stamp's work, issued only to Active Managers (resolved during specification).
- Q: How is the interplay between the Manager and the platform verified? → A: An automated end-to-end test in the verification environment; no manual run, so the pull request can merge without review (resolved during specification).
- Q: Should a Club Admin's revocation cut off the Manager's queue access immediately, or only when its short-lived credentials expire? → A: Immediately: on revocation the queue refuses the Manager's current credentials and disconnects it.
- Q: When revocation fences a Manager's running attempts, should those attempts count against each Logical Job's attempt budget? → A: Yes, like any stale attempt: each revocation-fenced attempt consumes one budget entry.
- Q: May the simulated Analyst be used outside development and test? → A: No: it is selectable only in development and test configurations, and any other Manager refuses to start with it.
- Q: Does this slice include the platform's upper concurrency limit per Manager? → A: Yes: the stamp configures a maximum concurrency per Manager, and the Manager receives it with its queue credentials.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A registered Manager executes ready analysis work end to end (Priority: P1)

A Coach finalizes a recording set and the stamp's analysis workflow makes Logical Jobs ready. An Active Analyst Manager in Running whose validated runtime offers the required capability pulls one of those jobs from the stamp's work queue, claims it, and receives a new Execution Attempt with a lease and fencing token. It runs the attempt through its runtime adapter boundary, here against a simulated Analyst, keeps the lease alive with heartbeats while the attempt runs, and reports exactly one completion with the attempt's fencing token, the job's pinned digests, and a result reference. The platform accepts the result and readiness moves on to dependent work.

**Why this priority**: This is the first time work flows from the platform through a real Manager and back. Every later Analyst capability, runtime adapter, and result pipeline builds on this loop, and it proves that registration, the durable workflow, and the Manager fit together.

**Independent Test**: Can be fully tested by registering a Manager against a stamp in a validation environment, making a Logical Job ready, letting the Manager run it against a simulated Analyst configured to succeed, and confirming that exactly one attempt exists, its lease was renewed while it ran, and exactly one accepted result with the pinned digests and the Manager's identity is recorded.

**Acceptance Scenarios**:

1. **Given** an Active Manager in Running below its concurrency limit and a ready Logical Job whose capability its validated runtime offers, **When** the job is delivered to the Manager, **Then** the Manager claims it, receives one new Execution Attempt with a lease and fencing token, and only then acknowledges the delivery.
2. **Given** a claimed attempt, **When** the simulated Analyst runs longer than one heartbeat interval, **Then** the Manager sends heartbeats with the attempt identity and fencing token often enough that the lease never expires while the attempt is healthy.
3. **Given** the simulated Analyst finishes successfully, **When** the Manager reports completion, **Then** the completion carries the attempt identity and number, fencing token, an idempotency key, the job's pinned scope, capability, schema, Analyst-profile, image, and model digests, and a result reference with checksum, and the platform records exactly one accepted result for the Logical Job.
4. **Given** the completion response is lost, **When** the Manager retries the same completion, **Then** it receives the original outcome and no second result is recorded.
5. **Given** the claim response is lost, **When** the Manager retries the claim for the same delivery, **Then** it receives the same attempt, lease, and fencing token, and no additional attempt is created or budget consumed.
6. **Given** a delivery whose Logical Job is no longer ready (already claimed, completed, blocked, or superseded), **When** the Manager claims it, **Then** the claim is rejected as obsolete, the Manager acknowledges the delivery, and it starts no work for it.
7. **Given** an Active Manager, **When** it needs access to the work queue, **Then** it obtains short-lived credentials of its own, limited to pulling and acknowledging its stamp's work, together with the stamp's maximum concurrency per Manager, through its authenticated platform API, and never stores a durable queue secret; a revoked or inactive Manager obtains none.

---

### User Story 2 - The host operator controls how much work the Manager takes (Priority: P2)

The host operator decides how much of the machine the Manager may use. The Manager pulls new work only while it is Running, below the effective concurrency limit, and its runtime preflight passes. Pausing, including a timed pause, stops new pulls immediately but lets active attempts finish with their heartbeats and completions. Changing the concurrency limit takes effect for new pulls only. The status window and the CLI show each active attempt and when its lease was last renewed.

**Why this priority**: Host operators contribute their machines voluntarily. Without dependable local control over admission, they cannot trust the Manager with real work. This story depends on Story 1 for the work it controls.

**Independent Test**: Can be fully tested by making more jobs ready than the configured limit, observing that the Manager never holds more active attempts than the limit, then pausing, resuming, and changing the limit while attempts are active, and checking the active attempts shown in the status window and the CLI.

**Acceptance Scenarios**:

1. **Given** an effective concurrency limit of N and more than N ready compatible jobs, **When** the Manager runs, **Then** it never holds more than N active attempts and pulls the next job only after an active attempt has ended.
2. **Given** a Running Manager with active attempts, **When** the host operator pauses it, **Then** no new delivery is pulled or claimed from that moment, and the active attempts continue to heartbeat and complete.
3. **Given** a timed pause, **When** its end time passes, **Then** the Manager resumes pulling within the bounds of its limit, its registration status, and its runtime preflight.
4. **Given** active attempts, **When** the host operator lowers the concurrency limit below the active count, **Then** no active attempt is cancelled and no new job is pulled until the active count falls below the new limit; **and When** the limit is raised, **Then** additional jobs are pulled up to the new limit.
5. **Given** a Manager whose runtime becomes unavailable, **When** preflight fails, **Then** it stops pulling new work, enters Runtime unavailable, and keeps heartbeating any attempt that is still running until it ends or is fenced.
6. **Given** a delivery for a capability the Manager's validated runtime does not offer, or whose resource requirements the host cannot meet, **When** it reaches the Manager, **Then** the Manager neither claims nor completes it and leaves it available for other Managers.
7. **Given** active attempts, **When** the host operator opens the status window or runs the CLI status command, **Then** each active attempt is shown with its Logical Job, attempt number, execution state, start time, and the time of its last successful heartbeat.

---

### User Story 3 - Failures never produce duplicate or stale results (Priority: P3)

Work fails in many ways: the Analyst reports a failure, hangs past its execution timeout, or crashes; the Manager loses its connection to the platform or is killed; the platform fences an attempt after its lease expired. In every case the Manager reports what it knows exactly once, abandons attempts the platform has fenced, never decides on its own to retry an attempt, and leaves recovery to the platform's lease expiry and retry rules.

**Why this priority**: At-least-once delivery and real hosts mean duplicate physical work will happen. The platform already guarantees that only one result is accepted; this story makes the Manager behave correctly around that guarantee so failures recover without manual intervention.

**Independent Test**: Can be fully tested by configuring the simulated Analyst to fail, hang, or crash, by interrupting the Manager's connection to the platform, and by stopping the Manager mid-attempt, then confirming the resulting attempt states, retries by the same or another Manager, and that exactly one accepted result exists per completed Logical Job.

**Acceptance Scenarios**:

1. **Given** the simulated Analyst reports a failure, **When** the attempt ends, **Then** the Manager reports a failed completion for the current attempt once, and the platform makes the Logical Job ready again only while attempt budget remains.
2. **Given** the simulated Analyst runs past the job's execution timeout, **When** the timeout elapses, **Then** the Manager stops it, cleans up the simulated work, and reports a failed completion that names the timeout.
3. **Given** the simulated Analyst crashes, **When** the Manager notices, **Then** it cleans up and reports a failed completion for the current attempt once.
4. **Given** heartbeats cannot reach the platform, **When** the lease is still valid, **Then** the Manager retries with bounded backoff, keeps the attempt running, and never assumes the lease was renewed.
5. **Given** a heartbeat or completion is rejected because the attempt is stale or fenced, **When** the Manager receives that answer, **Then** it stops the attempt's simulated work, cleans up, sends no further heartbeat or completion for it, and frees its concurrency slot.
6. **Given** the Manager process is stopped or the host fails while attempts are active, **When** the leases expire, **Then** the platform fences those attempts and makes their jobs ready again, and a Manager that later claims them executes the same pinned Logical Job.
7. **Given** a Manager restarts after it was stopped with active attempts, **When** it starts, **Then** it does not resume, heartbeat, or complete attempts of its previous process, and those attempts recover through lease expiry.

---

### User Story 4 - Draining, exiting, unregistering, and revocation respect active work (Priority: P4)

A host operator who drains, exits safely, or unregisters the Manager expects active attempts to finish first, within a bounded drain time. A Club Admin who revokes a Manager expects the opposite: an immediate security stop. Revocation fences all of that Manager's active attempts on the platform at once, the Manager stops and cleans up its work as soon as it observes the revocation, and every later heartbeat or completion from it is refused.

**Why this priority**: These are the existing local controls and the revocation path from the registration feature, extended to Managers that now hold real attempts. They depend on Stories 1 to 3.

**Independent Test**: Can be fully tested by starting drain, safe exit, and local unregister with active attempts and observing that the attempts finish or are handled by the drain-timeout policy before the Manager exits or unregisters, and by revoking a Manager with active attempts and observing that those attempts are fenced immediately and their jobs become ready for other Managers.

**Acceptance Scenarios**:

1. **Given** active attempts, **When** the host operator requests safe exit with drain, **Then** the Manager pulls nothing new, lets active attempts complete with heartbeats and completions, and exits once none remain.
2. **Given** the drain timeout expires with attempts still active and the policy is to cancel the request, **When** the Manager applies it, **Then** the drain is cancelled, the Manager returns to its previous intent, and the attempts continue.
3. **Given** the drain timeout expires with attempts still active and the policy is to stop and clean up, **When** the Manager applies it, **Then** it stops the remaining simulated work, cleans up, and reports a failed completion for each remaining current attempt before it exits or continues unregistering.
4. **Given** active attempts, **When** the host operator unregisters the Manager locally, **Then** the Manager drains as in scenarios 1 to 3 and asks the platform to revoke its registration only when the drain was not cancelled.
5. **Given** a Manager with active attempts, **When** a Club Admin revokes its registration, **Then** the platform fences every active attempt of that Manager in the same step, makes each Logical Job ready again while attempt budget remains, the queue disconnects the Manager and refuses its current credentials, and the platform refuses every later heartbeat and completion from that Manager.
6. **Given** a revoked Manager that is still running work, **When** it observes the revocation through a refused request or its status check, **Then** it stops and cleans up all simulated work, sends no further heartbeat or completion, and clears its stamp state as the registration feature specifies.

---

### Edge Cases

- A delivery arrives while the Manager is pausing, draining, or Runtime unavailable: it is not claimed and stays available for other Managers.
- The same Logical Job is delivered twice to one Manager, or to two Managers at once: exactly one claim succeeds; the other is rejected as obsolete and its delivery is acknowledged without work.
- A claim fails transiently (timeout, platform unavailable): the Manager does not acknowledge the delivery and either retries the claim with the same retry key or lets the delivery become available again; it never starts work without a successful claim.
- A heartbeat succeeds on the platform but its response is lost: the next heartbeat renews again; the Manager tracks the last heartbeat it saw succeed, not the last one it sent.
- The simulated Analyst finishes while a heartbeat is being retried: the Manager stops heartbeating and reports completion; if the attempt was fenced meanwhile, the completion is rejected as obsolete and nothing changes.
- The concurrency limit is set to zero or a negative value: the input is refused as invalid. A host-local limit above the stamp's maximum is accepted but has no effect beyond that maximum, and the status shows the effective limit.
- The queue credentials expire during a pull: the Manager obtains new credentials through its authenticated API before pulling again; active attempts are unaffected because heartbeats and completions do not depend on queue access.
- The platform clock and the host clock differ: lease expiry is decided only by the platform; the Manager derives its heartbeat cadence from the job's lease values, not from its own clock comparisons.
- A Club Admin revokes a Manager whose attempt was the Logical Job's last permitted attempt: the revocation-fenced attempt consumes that budget entry, the Logical Job fails permanently, and descendant-blocking and run-outcome rules apply.
- A Manager is started outside a development or test configuration with the simulated Analyst selected: it refuses to start, reports that the simulated Analyst is validation-only, and pulls no work.
- A completion for a job whose attempt budget is already exhausted by an earlier stale attempt: it is rejected as obsolete and the Logical Job's permanent failure stands.

## Requirements *(mandatory)*

### Functional Requirements

#### Work acquisition

- **FR-001**: The Analyst Manager MUST pull work only from the work queue of the stamp it is registered with, and only while its registration is Active, its operating state is Running, its runtime preflight has passed, and its active attempt count is below its effective concurrency limit.
- **FR-002**: The Analyst Manager MUST pull at most one delivery per free concurrency slot, so that the number of active attempts never exceeds the effective concurrency limit.
- **FR-003**: The Analyst Manager MUST claim a delivery only when its validated runtime offers the job's capability and the host can satisfy the job's pinned resource requirements; it MUST leave other deliveries unclaimed and available to other Managers.
- **FR-004**: Every claim MUST carry a retry key unique to the delivery and the Manager, so that retrying a claim after a lost response returns the same attempt as specified by the durable analysis workflow.
- **FR-005**: The Analyst Manager MUST acknowledge a delivery only after the platform has returned a successful or deterministically obsolete claim outcome, and MUST start no work for a delivery without a successful claim.
- **FR-006**: The Analyst Manager MUST obtain access to the work queue through its authenticated platform API as short-lived credentials issued to that Manager, scoped to its stamp's work and limited to pulling and acknowledging deliveries; it MUST NOT store a durable queue secret. The platform MUST issue such credentials only to Managers whose registration is Active. When a Manager's registration is revoked, the queue MUST refuse that Manager's current credentials and disconnect it immediately, in addition to their short configured lifetime.

#### Execution

- **FR-007**: The Analyst Manager MUST run each claimed attempt through its runtime adapter boundary, executing the Logical Job's pinned Analyst profile, image, and model digests unchanged; it MUST NOT substitute an image or model or choose a preprocessing strategy.
- **FR-008**: This feature MUST provide a simulated Analyst behind the runtime adapter boundary that can be configured per capability to succeed with a result reference and checksum, fail with a reason, run for a given time, hang, or crash, so that every outcome in this specification can be exercised without a container, model, or object storage. The simulated Analyst MUST be selectable only in development and test configurations; a Manager started with any other configuration that selects it MUST refuse to start and report why.
- **FR-009**: The Analyst Manager MUST enforce the job's pinned execution timeout: when it elapses, it MUST stop the attempt's work, clean up, and report a failed completion that names the timeout.

#### Heartbeats and fencing

- **FR-010**: While an attempt is active, the Analyst Manager MUST send heartbeats carrying its Manager identity, the Logical Job, the attempt, the fencing token, a timestamp, and the execution state, at a cadence derived from the job's pinned lease values so that a healthy attempt's lease never expires.
- **FR-011**: When a heartbeat fails for a transient reason, the Analyst Manager MUST retry with bounded backoff while the lease may still be valid, MUST NOT assume renewal, and MUST record the time of the last heartbeat that the platform confirmed.
- **FR-012**: When the platform rejects a heartbeat or completion as stale, fenced, or obsolete, the Analyst Manager MUST stop the attempt's work, clean up, send nothing further for that attempt, and free its concurrency slot.
- **FR-013**: The Analyst Manager MUST NOT decide that an attempt should be retried or re-enqueued; recovery of attempts that end without an accepted completion MUST rely on the platform's lease expiry and retry rules.
- **FR-014**: After a restart, the Analyst Manager MUST NOT resume, heartbeat, or complete attempts of a previous process; those attempts MUST recover through lease expiry.

#### Completion

- **FR-015**: The Analyst Manager MUST report exactly one completion per attempt that ends while it is current, with a stable idempotency key, the attempt identity and number, the fencing token, the outcome (succeeded or failed with a reason), the pinned scope, capability, schema, Analyst-profile, image, and model digests, and, for success, the result reference and checksum from the Analyst, in the versioned completion contract.
- **FR-016**: When a completion's response is lost, the Analyst Manager MUST retry the identical completion with the same idempotency key until it receives an outcome or the attempt is rejected as obsolete.
- **FR-017**: A failed completion MUST end the current attempt so that the platform makes the Logical Job ready again only while its attempt budget remains, as the durable analysis workflow specifies for explicitly failed attempts.

#### Local controls

- **FR-018**: Pause, including a timed pause, MUST stop new pulls and claims immediately while active attempts continue to heartbeat and complete; resume and the end of a timed pause MUST restore pulling subject to FR-001.
- **FR-019**: Changing the host-local concurrency limit MUST affect only new pulls: lowering it MUST NOT cancel active attempts, and raising it MUST allow additional pulls up to the new limit. The limit MUST be a positive integer. The stamp MUST configure a maximum concurrency per Manager, which the Manager receives together with its queue credentials (FR-006); the effective limit MUST be the lowest of the host-local limit, the stamp's maximum, and the host's validated capability, and a changed stamp maximum MUST take effect when the Manager next obtains credentials, with the same rules for active attempts.
- **FR-020**: When runtime preflight fails, the Analyst Manager MUST stop pulling new work and enter Runtime unavailable while it keeps heartbeating attempts that are still running until they end or are fenced.
- **FR-021**: Drain, safe exit, and local unregister MUST stop new pulls and wait for active attempts to complete within the configured drain timeout. When the timeout expires, the Analyst Manager MUST apply the configured drain-timeout policy: with *cancel the request*, the drain is cancelled, the Manager returns to its previous operating intent, and active attempts continue (a cancelled unregister leaves the registration Active, as the Analyst Manager registration feature specifies); with *stop and clean up*, it stops the remaining work, cleans up, and reports a failed completion naming the drain timeout for every remaining current attempt before exiting or continuing to unregister.
- **FR-022**: The status window and the CLI status output MUST show each active attempt with its Logical Job, attempt number, execution state, start time, and the time of its last confirmed heartbeat, together with the active attempt count and the effective concurrency limit.

#### Revocation

- **FR-023**: When a Club Admin revokes a Manager's registration, the platform MUST, in the same durable step, fence every active attempt of that Manager, count each fenced attempt against its Logical Job's attempt budget exactly like a stale attempt, and make each affected Logical Job ready again only while budget remains (otherwise it fails permanently); it MUST refuse every later claim, heartbeat, completion, and queue credential request from that Manager, and the queue MUST disconnect the Manager and refuse its current credentials immediately (FR-006).
- **FR-024**: When the Analyst Manager observes revocation through any refused request or its registration-status check, it MUST stop and clean up all active work immediately, without draining, send no further heartbeat or completion, and then clear its stamp state as the Analyst Manager registration feature specifies.

#### Observability

- **FR-025**: The Analyst Manager MUST log claims, heartbeat failures, fencing, completions, and cleanup with the Logical Job, attempt, and correlation identities, and MUST NOT log fencing tokens, queue credentials, access tokens, or result checksums in full.

### Key Entities *(include if feature involves data)*

- **Delivery**: One delivery of a ready Logical Job from the stamp's work queue to a Manager; it confers no authority on its own and is acknowledged only after the claim outcome is known.
- **Active Attempt (Manager view)**: The Manager's local record of an attempt it currently runs: Logical Job, attempt identity and number, fencing token, lease values, execution state, start time, last confirmed heartbeat, and the idempotency key of its completion. It lives only in the running process.
- **Simulated Analyst Profile**: Validation-only configuration that tells the simulated Analyst, per capability, which outcome to produce and how long to run; it cannot be selected outside development and test configurations.
- **Queue Access**: The short-lived, per-Manager credentials to pull and acknowledge its stamp's deliveries, obtained through the authenticated API together with the stamp's maximum concurrency per Manager.
- **Effective Concurrency Limit**: The lowest of the host-local limit, the stamp's configured maximum per Manager, and the host's validated capability; the number of attempts a Manager may run at once.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the validation environment, 100% of ready Logical Jobs for a capability offered by a Running Manager reach an accepted result through that Manager when the simulated Analyst is configured to succeed, each within 30 seconds of becoming ready plus the configured simulated run time.
- **SC-002**: Across all concurrency tests, a Manager holds more active attempts than its effective concurrency limit in 0 observations.
- **SC-003**: After pause, a Manager claims 0 new attempts, and 100% of attempts that were active when the pause began still complete with a reported outcome.
- **SC-004**: In 100% of failure, hang, crash, connection-loss, restart, and fencing tests, each completed Logical Job has exactly one accepted result, and no attempt is completed after the platform fenced it.
- **SC-005**: In 100% of revocation tests, every active attempt of the revoked Manager is fenced in the same step as the revocation, and the revoked Manager has stopped all of its work within 10 seconds of observing the revocation.
- **SC-006**: In 100% of tests where a healthy attempt runs longer than three lease periods, its lease is never expired by the platform.
- **SC-007**: 100% of the acceptance scenarios in this specification can be executed automatically without an interactive desktop session, an Analyst container, a model, object storage, or a production container runtime.
- **SC-008**: 0 durable queue secrets, fencing tokens, or access tokens appear in the Manager's persisted state or logs.
- **SC-009**: In 100% of credential tests, Managers that are not Active obtain no queue credentials, and a revoked Manager is disconnected from the queue and refused on reconnect within 5 seconds of the revocation.
- **SC-010**: The automated end-to-end test runs the full loop (pull, claim, heartbeat, completion, fencing, and revocation) between the Manager and the platform in the verification environment and passes on every merge.

## Assumptions

- **Actors**: The host operator controls the Manager locally (pause, resume, timed pause, concurrency limit, drain, safe exit, unregister). The Club Admin revokes registrations on the platform. Coaches trigger analysis runs by finalizing recording sets, as the durable analysis workflow specifies; no human acts on individual attempts.
- **Simulated Analyst only**: Execution runs through the runtime adapter boundary against a simulated Analyst. Real Docker or Podman runtime adapters, image pull and digest resolution, container isolation, constrained mounts, orphan reconciliation, and cleanup of real containers are out of scope and follow in a runtime-adapter slice.
- **Result references**: The simulated Analyst produces a result reference and checksum without uploading anything. Result upload, result manifests in object storage, object-storage grants, and manifest-content validation are out of scope; the platform's completion acceptance already validates fencing, identity, pinned digests, contract shape, and declared lineage without reading manifest contents.
- **Capability matching**: The Manager matches deliveries against the capabilities its validated (simulated) runtime offers and the job's pinned resource requirements. Advertising capabilities or host hardware to the platform, beyond what the queue needs to deliver compatible work, is out of scope.
- **Execution timeout**: The pinned execution timeout in each Logical Job snapshot is enforced by the Manager, which owns the running attempt; lease expiry remains the platform's decision.
- **Restart behavior**: Without real containers there is nothing to reconcile after a Manager restart; attempts of a previous process simply expire. Reconciling surviving containers after restart belongs to the runtime-adapter slice.
- **Non-production values**: Lease, heartbeat, retry, backoff, execution-timeout, drain-timeout, concurrency bounds, queue-access lifetimes, and credential lifetimes used in development and tests are explicit configuration, not adopted production values, as the production operations architecture requires.
- **Out of scope**: the Manager status report and the Web UI fleet view; queue-depth display; host utilization reporting; the Analyst SDK and any capability implementation; model execution; installers and updates; the headless Linux profile; cancellation or supersession of runs.
- **Verification across both solutions**: The platform and the Manager live in separate solutions. Their interplay (pull, claim, heartbeat, completion, fencing, and revocation) is verified by an automated end-to-end test in the verification environment that runs the platform, the work queue, and the database in disposable containers and the Manager against them, in addition to the shared contract examples and each side's own tests. No manual run is required before merge, so the pull request can merge without a person's review.
- **Dependencies**: `specs/archive/20261005-130704-analyst-manager-registration` provides Manager machine authentication, operating states, the runtime adapter boundary with a simulated runtime and preflight, local controls, and revocation observation. `specs/archive/20261005-130703-durable-analysis-workflow` provides Logical Jobs, the claim, heartbeat, and completion operations, retry keys, idempotent completion, lease expiry, reliable publication to the work queue, and the versioned job and completion contracts. Through them, the feature also relies on `specs/archive/20261005-130700-platform-persistence-foundation` and `specs/archive/20261007-115855-environment-verification-coverage`.
- **Revocation fencing**: The durable analysis workflow left fencing at the moment of revocation to a later slice; this feature adds it (FR-023), because Managers now hold real attempts.
- **Evidence**: Passing validation of this feature is development evidence only and does not establish deployment support or production readiness.
- **Architecture References**: `docs/architecture/analyst-runtime-and-recovery.md` (Execution Attempts, Heartbeats, And Recovery; Registration And Runtime Credentials; Analyst Manager Responsibilities), `docs/architecture/analyst-manager.md` (Analyst Manager; Machine Authentication; Capacity And Pause Controls), `docs/architecture/job-processing.md` (Durable Workflow Model; Run State And Recovery), `docs/architecture/contracts-and-compatibility.md`, `docs/architecture/production-operations.md`.
