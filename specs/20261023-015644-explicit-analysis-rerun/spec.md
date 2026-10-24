# Feature Specification: Explicit Analysis Run Request

**Feature Branch**: `20261023-015644-explicit-analysis-rerun`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Club Admins and Coaches of the owning team can explicitly request an analysis run for a match whose recording set is finalized, so that analysis can be repeated after the stamp's default workflow, an approved Analyst profile, or an approved image changed, without uploading a new recording-set version. One member operation under /api/v1 asks the Scheduler for a run of the match's current finalized recording-set version and returns 202 with the run identity and a conditional-read link. It reuses the existing run creation of the recordings-finalized consumer and the automatic supersession of the match's older runs; a request whose recording-set version and resolved workflow version already have a run returns that run instead of creating a duplicate; a match without a finalized recording set, an unresolvable default workflow, or an unauthorized caller is refused with a problem result. The same durable receipt and reconciliation behavior as the finalization path applies, and request outcomes appear in the workflow operations summary where the finalization path already does. Out of scope: any Web UI control, descendant-only recomputation, choosing a workflow or Analyst versions per request, scheduled or bulk requests, importing prior results, changes to run cancellation, supersession, or result reads, MCP or agent operations, and production values."

## Clarifications

### Session 2026-10-23

- Q: Which refused requests are recorded as a durable receipt with the outcome `rejected` (and so appear in the operations summary with a rejection category), and which are refused without any receipt? → A: A request with no finalized recording set and a request whose default workflow cannot be resolved are both recorded as `rejected` with their category; authorization, validation, and dependency failures leave no receipt.
- Q: When the default workflow is changed back to a workflow version that already has a run for the current recording set, but a newer run has since superseded it, what should an explicit request do? → A: Return the existing run with `created: false`, and also include the match's current run so the member can see which run is current.
- Q: Does a repeat of an already answered request with the same idempotency key record another security audit event? → A: No, a replay returns the recorded answer and records no further audit event, so each request identity has exactly one audit event.
- Q: When a request that was refused and recorded as `rejected` (no finalized recording set, or no resolvable default workflow) is repeated with the same idempotency key after the cause was fixed, what should the platform answer? → A: Return the originally recorded refusal without re-evaluating anything; the member must use a new key to try again after fixing the cause.

## User Scenarios & Testing *(mandatory)*

The actors are Club Admins and Coaches of the team that owns a match, who may change the team's analysis, and Viewers, who may only read it. The platform side implements the "Explicit analysis demand" path of the analysis request flow in [Job Processing](../../docs/architecture/job-processing.md), the complete-rerun rule, and the run-creation behavior of the [Durable Workflow Model](../../docs/architecture/job-processing.md#durable-workflow-model). The Scheduler creates the run exactly as it does when a recording set is finalized.

### User Story 1 - Repeat the analysis of a finalized match (Priority: P1)

After a Club Admin approved a new Analyst profile version or changed the stamp's default workflow, a Coach wants the match analyzed again with the changed setup, but the recordings have not changed and no new recording-set version will be uploaded. The Coach asks for an analysis run of the match. The platform resolves the stamp's current default workflow, creates a complete new run for the match's current finalized recording-set version with its snapshot-fixed graph, requests the media segments the workflow needs, starts readiness evaluation, and answers that the request was accepted with the run's identity and a link from which the run's status can be read conditionally. The match's older runs are superseded automatically, as they are when a newer recording set is finalized.

**Why this priority**: It is the only way to get a fresh analysis after a registry change without re-uploading recordings, and the whole feature is this transition.

**Independent Test**: With a match whose recording set is finalized and whose latest run used an earlier workflow version, change the default workflow to a new version, then have a Coach of the owning team request analysis; a new run appears for the same recording-set version and the new workflow version, its status is readable through the returned link, the previous run is superseded with the new run as its successor, and a security audit event records the request.

**Acceptance Scenarios**:

1. **Given** a match with a finalized recording set and a resolvable default workflow for which no run exists yet, **When** a Club Admin or a Coach of the owning team requests analysis, **Then** the platform answers `202 Accepted` with the new run's identity and a link to the run's status read, and a run in state `Requested` or later exists for the current recording-set version and the resolved workflow version.
2. **Given** the new run, **When** its snapshot and jobs are inspected, **Then** they were created by the same run creation as the finalization path: the workflow definition, Analyst profiles, images, and policies resolved at creation are pinned in the run snapshot, the complete graph is planned (no descendant-only subset), segments needed by `required` and `optional` video policies are requested, and readiness evaluation and outbox publication proceed as for any run.
3. **Given** older runs of the same match, **When** the new run is created, **Then** the automatic supersession of the existing behavior applies unchanged: active older runs are cancelled with cause `superseded` and every older run gets its immutable supersession record naming the new run.
4. **Given** a successful request, **When** the audit records are inspected, **Then** exactly one security audit event names the requesting member, the match, the recording-set version, and the resulting run and outcome.
5. **Given** a Viewer of the owning team, **When** the Viewer requests analysis, **Then** the platform refuses as forbidden; **Given** a member without a role on the owning team, **Then** the platform answers as not found and discloses nothing about the match; **Given** an unauthenticated caller, **Then** the platform refuses as unauthenticated. No run, receipt, or audit event of a successful request is created in any of these cases.

---

### User Story 2 - A repeated request never duplicates a run (Priority: P1)

A Coach asks for analysis twice, because the first answer was lost or because the setup had not changed in between. The platform never creates two runs for the same recording-set version, workflow, and workflow version: it returns the run that already exists. Retrying the same request, for example after a network failure, yields the same answer.

**Why this priority**: A request that can create duplicate runs would waste Manager capacity and break the run uniqueness the platform relies on; it is part of the minimum correct behavior.

**Independent Test**: Request analysis twice for the same match without any registry change; both answers name the same run and only one run exists. Retry a request with the same idempotency key; the answer is identical. Run two simultaneous requests; exactly one run exists and both answers name it.

**Acceptance Scenarios**:

1. **Given** a run already exists for the match's current recording-set version and the currently resolved workflow and workflow version, **When** an authorized member requests analysis, **Then** the platform answers `202 Accepted` with that run's identity, states that no new run was created, creates no run, performs no supersession, and starts no new segment requests.
2. **Given** a request that was answered, **When** the same request is repeated with the same idempotency key, **Then** the platform returns the originally recorded run, created flag, and current run identity without re-evaluating the registry, even if the default workflow changed in between; the run's state is read at replay time.
   Likewise, a request that was refused and recorded as `rejected` and is repeated with the same idempotency key after the cause was fixed (a recording set finalized or a default workflow resolvable) returns the originally recorded refusal without re-evaluating anything; the member uses a new key to try again.
3. **Given** an idempotency key already used for a request on another match or by another member, **When** it is used again, **Then** the platform refuses with the existing idempotency-conflict problem result and changes nothing.
4. **Given** two requests for the same match that run at the same time, **When** both complete, **Then** exactly one run exists for the recording-set version, workflow, and workflow version, and both answers name it.
5. **Given** the finalization path and an explicit request racing for the same recording-set version and workflow version, **When** both complete, **Then** exactly one run exists and each path reports it as created or existing according to which one created it.
6. **Given** a run that exists for the same recording-set version but for an earlier workflow version, **When** an authorized member requests analysis after the default workflow changed, **Then** a new run is created (the uniqueness includes the workflow version) and the earlier run is superseded.
7. **Given** the current run was cancelled or failed, **When** a member requests analysis and the resolved workflow version is unchanged, **Then** the existing run is returned; the platform does not create a second run of the same recording-set version and workflow version, and the answer shows the run's state so the member can see why nothing new started.
8. **Given** the default workflow was changed back to a workflow version whose run for the current recording-set version was superseded by a newer run, **When** an authorized member requests analysis, **Then** the platform returns the superseded run with `created: false`, creates no run and performs no supersession, and the answer also names the match's current run so the member can see which run is current.

---

### User Story 3 - Requests that cannot start an analysis are refused clearly (Priority: P1)

Some requests cannot be honored: the match has no finalized recording set, the stamp's default workflow cannot be resolved from the registry, or a precondition of the request is missing. The platform refuses each of them with a problem result that names the reason, creates no run, and leaves existing runs untouched.

**Why this priority**: Without clear refusals, members cannot tell a lost request from an impossible one, and the platform could create runs that can never succeed.

**Independent Test**: Request analysis for a match with no finalized recording set; for a match whose recordings are uploaded but not finalized; and while the Registry has no resolvable default workflow; each is refused with its own problem code and nothing changes. Omit the idempotency key; the request is refused.

**Acceptance Scenarios**:

1. **Given** a match with no finalized recording-set version, **When** an authorized member requests analysis, **Then** the platform refuses with a conflict problem result that names the missing finalized recording set, records a durable receipt with the outcome `rejected` and that category, and no run is created.
2. **Given** a Registry without a resolvable default workflow (none set, retired, or with an unapproved Analyst profile or image), **When** an authorized member requests analysis, **Then** the platform refuses with a problem result that names the workflow resolution category, records a durable receipt with the outcome `rejected` and that category, and no run is created and no existing run changes.
3. **Given** a request without the required idempotency key or with a malformed one, **When** it is sent, **Then** the platform refuses with the existing shared problem results and records no receipt.
4. **Given** a match identity that does not exist, **When** a request names it, **Then** the platform answers as not found.
5. **Given** a workflow that resolves but whose graph fails the run-creation validation, **When** a member requests analysis, **Then** the behavior is the same as in the finalization path: the run is recorded as `Failed` with the validation reason, supersession applies as there, and the answer names that run.
6. **Given** any refusal, **When** the match's runs and the audit records are inspected, **Then** no run was created or changed by the request.

---

### User Story 4 - The request is durable and visible to operations (Priority: P2)

A Club Admin looking at the workflow operations summary wants to see that analysis was requested, by which path, and what came of it, and the platform must recover from interruptions the same way as for finalization. Each request that reaches a decision (a run created, a run returned, or a rejection for a missing finalized recording set or an unresolvable default workflow) leaves a durable receipt with its outcome so that a repeated or interrupted request resolves to one recorded outcome, and the operations summary and receipt list show the outcomes of explicit requests next to those of the finalization path, distinguishable by their message type.

**Why this priority**: The operations surface and recovery guarantees are required by the architecture for run creation, but the core request works without them.

**Independent Test**: Make one request that creates a run, one that returns an existing run, and one that is rejected; the operations summary counts and the receipt list show all three with their outcome, the explicit-request message type, the recording-set version, and the run; a request interrupted after run creation but before the answer is repeated with the same key and returns the created run without a second one; the analysis reconciliation reports and repairs explicit-request runs the way it does for finalization runs.

**Acceptance Scenarios**:

1. **Given** a request that reached a decision, **When** the receipt store is inspected, **Then** one receipt exists per request identity with the message type of explicit requests, the recording-set version if one exists, the outcome, the run if any, the rejection category if any, and the receipt time, written in the same transaction as the run creation or the decision to return an existing run or to reject the request.
2. **Given** a Club Admin, **When** the operations summary and the notification receipts are read, **Then** receipts of explicit requests are counted and listed with those of the finalization path, can be filtered by outcome as before, and show their message type; message detail never exposes payloads.
3. **Given** a process restart or a failure between run creation and the answer, **When** the same request is repeated with its idempotency key, **Then** it yields the recorded outcome and no second run.
4. **Given** the existing analysis reconciliation, **When** it inspects runs created by explicit requests, **Then** it treats them like any other run: it neither creates nor deletes runs for them, and its outcome is unchanged for runs of both paths.
5. **Given** a rejected request, **When** the operations summary is read, **Then** it appears with its rejection category in the same rejected counts and lists as finalization rejections.

---

### Edge Cases

- A new recording-set version was finalized and its finalization notification is still unprocessed when a member requests analysis: the request uses the match's current finalized version; whichever of the two paths creates the run first wins, and the other returns it.
- The default workflow changed between a member reading the match page and requesting analysis: the request always resolves the default workflow at request time.
- The match was finalized, then a later recording-set revision was started but not finalized: only the latest finalized version counts.
- The Registry is temporarily unavailable or the recording-set lineage cannot be read: the request is refused as a retryable dependency failure, no receipt is recorded, and repeating it with the same key is safe (it is evaluated afresh, because no answer was recorded).
- Many requests in quick succession by different members: each resolves to the same run until the resolved workflow version changes.
- The owning team or match was archived or deleted: the request is refused as it is for other match-scoped changes and creates nothing.
- Segment requests of the new run are rejected (for example, limits): the same per-job reason is recorded as in the finalization path and the run is still created.
- Supersession finds an older run already terminal: it is only marked as superseded by the new run, as in the existing behavior.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The platform MUST provide one member operation under `/api/v1` through which a Club Admin or a Coach of the team that owns a match requests an analysis run for the match, with no request body and no way to choose a workflow, Analyst profile, image, capability, or version.
- **FR-002**: The operation MUST follow the platform's member-API conventions: active-member authentication with the session anti-forgery check, problem results for every refusal, team-scoped authorization through the existing access authorizer and team-scope source, and the existing idempotency-key convention of state-creating match operations (the key is required; the `If-Match` precondition does not apply because the request addresses no versioned resource).
- **FR-003**: Viewers MUST be refused as forbidden, members without a role on the owning team and unknown matches MUST be answered as not found, and unauthenticated callers MUST be refused; none of these MUST create or change anything.
- **FR-004**: The request MUST use the match's current finalized recording-set version; it MUST NOT accept a recording-set version, and it MUST be refused with a conflict problem result, recorded as a `rejected` receipt with its category, when the match has no finalized recording set.
- **FR-005**: The platform MUST resolve the stamp's default workflow from the Registry at request time with the same resolution as the finalization path, and MUST refuse the request with a problem result naming the resolution category, recorded as a `rejected` receipt with that category, when it cannot be resolved.
- **FR-006**: A request for which a run already exists for the recording-set version, workflow, and workflow version MUST return that run, MUST NOT create a run, MUST NOT apply supersession, and MUST NOT request segments, regardless of the existing run's state, including a run that a newer run has superseded; in that case the answer MUST also name the match's current run.
- **FR-007**: Otherwise the platform MUST create the run through the same run creation as the recordings-finalized consumer: snapshot-fixed workflow, profiles, images, and policies, a complete graph (never a descendant-only subset), segment requests by the nodes' video policies, readiness evaluation, outbox publication, and run-validation failure recorded as a `Failed` run, so both paths are guaranteed to behave identically.
- **FR-008**: Run creation by explicit request MUST apply the existing automatic supersession of the match's older runs unchanged; this feature MUST NOT change supersession, cancellation, or result reads.
- **FR-009**: Run creation MUST be safe under concurrency, between requests and between a request and the finalization path: at most one run per recording-set version, workflow, and workflow version exists, and every caller receives it.
- **FR-010**: A successful request MUST answer `202 Accepted` with the run identity, whether the run was created by this request or already existed (`created`), the run's state, the match's current run when it differs from the returned run, and a link to the run's status read that supports conditional reads.
- **FR-011**: A repeated request with the same idempotency key by the same member for the same match MUST return the originally recorded run, created flag, and current run identity (the run's state is read at replay time), including the originally recorded refusal of a `rejected` request, without re-evaluating the recording set or the Registry (a new key is needed to try again after the cause was fixed); the same key for another match or member MUST be refused with the existing idempotency-conflict problem result.
- **FR-012**: Every request MUST be recorded as a durable receipt with its own message type, recording-set version, outcome (`run-created`, `run-exists`, or `rejected`), rejection category, run, and time; the receipt MUST be written in the same transaction as the run creation or the decision to return an existing run or to reject the request. Only the refusal of a match without a finalized recording set and the refusal of an unresolvable default workflow are recorded, as `rejected` with their category; refusals caused by authorization, validation, or dependency failures MUST leave no receipt, and a `rejected` receipt MUST NOT block a later valid request with a new key.
- **FR-013**: Dependency failures while resolving the workflow or reading the recording-set lineage MUST be reported as retryable problem results without recording a receipt.
- **FR-014**: The workflow operations summary and the notification receipt reads MUST include the receipts of explicit requests with the same outcome counts, filters, and paging, and MUST show the receipt's message type; message detail MUST NOT contain payloads.
- **FR-015**: A successful request, with either outcome, MUST record exactly one security audit event naming the member, the match, the recording-set version, the run, and the outcome; a replay with the same idempotency key returns the recorded answer and MUST NOT record a further audit event, so each request identity has exactly one audit event; refusals MUST NOT record the event of a successful request.
- **FR-016**: The existing analysis reconciliation and the recordings-finalized consumer MUST keep their behavior and treat runs created by explicit requests like any other run.
- **FR-017**: The new operation MUST appear in the committed OpenAPI contract with its problem results and idempotency-key parameter, and any machine-readable contract it adds MUST be validated like the existing ones.
- **FR-018**: The feature MUST NOT add a Web UI control, descendant-only recomputation, per-request workflow or version choice, scheduled or bulk requests, import of prior results into the new run, MCP or agent operations, or production values.

### Key Entities *(include if feature involves data)*

- **Analysis request**: A member's explicit demand for analysis of a match's current finalized recording set, identified by its idempotency key and member, resolved to exactly one receipt outcome.
- **Analysis run**: Existing entity; the run created or returned by a request, identified by recording-set version, workflow, and workflow version.
- **Notification receipt**: Existing durable record of a processed message and its outcome; gains the message type of explicit requests next to `recordings-finalized`.
- **Finalized recording-set version**: Existing entity; the match's latest finalized version, the only recordings an explicit request analyzes.
- **Security audit event**: Existing entity; records the requesting member, match, recording-set version, run, and outcome.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After the default workflow, an Analyst profile, or an image changed, an authorized member gets a complete new analysis of an unchanged match with one request and no new upload, and the request is answered within 2 seconds under normal development load (a single request on the standard test fixture: one match with one finalized recording set and the seeded default workflow).
- **SC-002**: Across any number of repeated or concurrent requests, including races with the finalization path, exactly one run exists per recording-set version, workflow, and workflow version (zero duplicates in the verification scenarios).
- **SC-003**: 100% of refusals (unauthorized, unknown match, no finalized recording set, unresolvable workflow, missing key) create no run and change no existing run.
- **SC-004**: A run created by an explicit request is indistinguishable in snapshot, graph, segment requests, readiness, and supersession behavior from one created by the finalization path for the same inputs.
- **SC-005**: A Club Admin sees every explicit request outcome in the workflow operations summary and receipt list, and a request interrupted at any point resolves to one recorded outcome when repeated.

## Assumptions

- The operation is a `POST` addressed to the match (for example, a collection of analysis runs under the match) and uses the `Idempotency-Key` header like the other state-creating match operations; the exact path and problem codes are planning decisions and the operation joins the committed OpenAPI snapshot.
- The same-key replay returns the recorded outcome, records no further audit event, and does not re-resolve the workflow; a new key re-resolves the default workflow, which is how a member repeats analysis after a registry change.
- Returning an existing run is a successful `202` answer with an explicit "created" indicator rather than an error, so repeated requests are harmless.
- The run-creation logic of the recordings-finalized consumer is shared by both paths rather than duplicated; the explicit path differs only in how it obtains the recording-set lineage (the match's current finalized version) and in its receipt identity and message type.
- A recording set's lineage from the explicit path carries the same members as the finalization notification would, so the existing lineage checks hold by construction.
- Runs that ended `Cancelled` or `Failed` for the same recording-set version, workflow, and workflow version are not replaced by a request; a new analysis becomes possible when the resolved workflow version changes, which is the stated purpose of the feature. Restarting an identical failed run is a different capability and out of scope.
- The reconciliation behavior is unchanged: the request creates its run synchronously, so no new reconciliation obligation appears beyond treating its receipts and runs like the finalization path's.
- Development and Test values only; production limits, rate limits, and retention of receipts remain unresolved.
- Depends on the implemented specs durable-analysis-workflow, analyst-registry, analysis-run-cancellation, analysis-run-supersession, partial-coverage-barriers, and materialized-segments; no open spec is required.
