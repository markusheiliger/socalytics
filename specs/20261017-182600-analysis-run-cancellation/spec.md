# Feature Specification: Analysis Run Cancellation

**Feature Branch**: `20261017-182600-analysis-run-cancellation`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Analysis Run Cancellation: the Durable Analysis Workflow made the `Cancelled` run state representable but provides no transition into it, and its spec deferred cancellation together with the revocation of queued work. The architecture's accepted semantics (docs/architecture/job-processing.md#run-state-and-recovery) say cancellation immediately stops publication, revokes queued work, advances or invalidates attempt fencing tokens, and rejects later completions, while stopping an active container is best effort and correctness relies on completion rejection. This feature lets a Club Admin or a Coach of the owning team cancel a `Requested` or `Running` analysis run through a member-facing `/api/v1` operation with optimistic concurrency (If-Match on the run's version) and an optional reason; the run becomes `Cancelled` with who, when, and why recorded and a security/audit event; ready and claimed logical jobs stop being published or are revoked from the work queue, current attempts are fenced so later heartbeats, result-grant requests, upstream reads, and completions from Managers are refused and never accepted, and Managers holding a fenced attempt stop the Analyst container and clean up as best effort (using the existing runtime adapter and fencing behavior of Analyst Manager Work Execution and the Docker Desktop runtime adapter). Already accepted results stay immutable history and remain readable; a cancelled run never transitions again; cancelling a terminal run is refused with a problem response, and repeating the same cancellation is idempotent. The run-state-changed event and outbox carry the transition. The Web UI shows a cancel action with confirmation on unfinished runs for authorized members (in the match page's analysis run list) and shows cancelled runs with their reason. Out of scope: automatic supersession when a new recording set is finalized (spec 4), rerunning or restarting runs, descendant-only recomputation, cross-run reuse, cancelling single nodes or jobs, production timeout values. Depends on Durable Analysis Workflow, Analyst Manager Work Execution, Analysis Result Manifests and Validated Acceptance, Analysis Result Read for Jobs, Docker Desktop Runtime Adapter for Analyst Execution, Web UI Match Workflow and Recording Upload (all merged; archived under specs/archive/). Implements docs/architecture/job-processing.md#run-state-and-recovery (cancellation), the fencing and late-completion rules in docs/architecture/analyst-runtime-and-recovery.md, and docs/architecture/client-applications.md#web-ui for the cancel action."

## Clarifications

### Session 2026-10-17

- Q: Which state should the unfinished logical jobs and workflow nodes of a cancelled run end in? → A: A new terminal `Cancelled` state for logical jobs and workflow nodes, reported in the run status read with an additive `cancelled` job count per node.
- Q: When is a repeated cancellation of an already cancelled run an idempotent success? → A: When an authorized member names either the run's version from before the cancellation or its current version; the cancelled run is returned unchanged, and any other version is a precondition failure.
- Q: How should the cancelling member be identified in the run reads and the Web UI? → A: By member id plus the member's current account name (the member's display name), resolved when the run is read; no name is copied into the run.

## User Scenarios & Testing *(mandatory)*

The actors are Club Admins and Coaches of the team that owns a match, who may change the team's analysis, and Viewers, who may only read it. The platform side implements the cancellation semantics of [Run State And Recovery](../../docs/architecture/job-processing.md#run-state-and-recovery) with the fencing and late-completion rules of [Execution Attempts, Heartbeats, And Recovery](../../docs/architecture/analyst-runtime-and-recovery.md#execution-attempts-heartbeats-and-recovery); the Web UI side adds the cancel action to the connected match workflow of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui).

### User Story 1 - Cancel an unfinished analysis run (Priority: P1)

A Coach notices that an analysis run was started for the wrong recordings, or is no longer needed, and cancels it. The run immediately becomes `Cancelled`; the platform records who cancelled it, when, and the optional reason the Coach gave, writes an audit event, and announces the state change like every other run transition. Cancelling the same run again with the same request has no further effect, and a run that has already ended cannot be cancelled.

**Why this priority**: It is the missing transition into the `Cancelled` state and the only way for members to stop analysis they no longer want; every other story builds on it.

**Independent Test**: Through the member API, a Coach of the owning team cancels a `Requested` run and a `Running` run with the run's current version and a reason; both become `Cancelled` with the recorded member, time, and reason, an audit event and a run-state-changed notification exist; a repeated request reports the same cancellation; a request with an outdated version, for a completed run, by a Viewer, or by a member of another team is refused without changing the run.

**Acceptance Scenarios**:

1. **Given** a `Requested` or `Running` run of a team's match, **When** a Club Admin or a Coach of that team cancels it with the run's current version and an optional reason, **Then** the run becomes `Cancelled`, records the cancelling member, the cancellation time as its end time, and the reason, and the response returns the run with its new version.
2. **Given** a cancelled run, **When** an authorized member repeats the cancellation with the run's version from before the cancellation or its current version, **Then** the platform reports the existing cancellation as success without changing the run, its version, or its reason, and without recording a second audit event or notification.
3. **Given** a run in `Completed`, `PartiallyCompleted`, or `Failed`, **When** a member tries to cancel it, **Then** the platform refuses with a conflict problem response that names the run's terminal state and changes nothing.
4. **Given** a version that is not the run's current version, or no version, **When** a member tries to cancel, **Then** the platform refuses with the existing precondition problem responses and changes nothing.
5. **Given** a Viewer of the owning team, **When** the Viewer tries to cancel, **Then** the platform refuses as forbidden; **Given** a member without a role on the owning team, **Then** the platform answers as not found and discloses nothing about the run.
6. **Given** a successful cancellation, **When** the run-state-changed notifications and audit records are inspected, **Then** exactly one notification for the transition to `Cancelled` and exactly one audit event naming the member, the run, and the outcome exist.

---

### User Story 2 - Cancelled work stops and can never be accepted (Priority: P1)

When a run is cancelled while Managers are working on it, no new work of the run reaches any Manager, queued work of the run is never started, and every attempt in progress is fenced: the Manager's next heartbeat, result-grant request, upstream read, or completion is refused as obsolete, so no result of the run is accepted after the cancellation. The Manager stops the Analyst container and cleans up its working data as best effort. Results the run accepted before the cancellation stay as they were.

**Why this priority**: Without it, cancellation would only be a label; the architecture requires that cancellation is enforced by fencing and completion rejection, not by the Manager's cooperation.

**Independent Test**: With the simulated runtime, a run with one claimed attempt and further ready and waiting jobs is cancelled; the ready jobs are never claimed, the claimed attempt's heartbeat, grant request, upstream read, and completion are all refused as obsolete, no new accepted result appears for the run, the Manager stops and removes the attempt's container and staged data, and accepted results that existed before stay readable and unchanged.

**Acceptance Scenarios**:

1. **Given** a run with ready logical jobs whose work messages are not yet published, **When** the run is cancelled, **Then** those messages are never published.
2. **Given** a run whose work messages are already in the work queue, **When** a Manager tries to claim one after the cancellation, **Then** the claim is refused as obsolete, the Manager acknowledges the message, and no attempt is created.
3. **Given** a run with an active attempt, **When** the run is cancelled, **Then** the attempt is closed in the same transaction as the cancellation, its fencing token no longer authorizes anything, and its later heartbeat, result-grant request, upstream read, and completion are refused as obsolete without changing any accepted result.
4. **Given** a Manager whose heartbeat or other call for the attempt was refused as obsolete, **When** it handles the refusal, **Then** it stops the Analyst container, deletes the attempt's staged inputs and result data, and reports no result, as best effort; the platform's correctness does not depend on this step.
5. **Given** results the run accepted before the cancellation, **When** members or jobs of other runs read them, **Then** they are unchanged, remain attributed to the cancelled run, and are never attributed to or reused by another run.
6. **Given** a completion that the platform committed before the cancellation, **When** the cancellation follows, **Then** that result stays accepted, and no readiness, publication, or node completion follows from it.
7. **Given** a cancelled run, **When** the reconciliation, publication, Job Monitor, or readiness workers run, **Then** they never publish, republish, requeue, claim, or transition any job, node, or the run again.

---

### User Story 3 - Cancel from the match page in the Web UI (Priority: P2)

On the match page, a Coach sees the analysis runs of the match as today and, for each run that is still requested or running, a cancel action. The Coach confirms the cancellation in a dialog, may enter a reason, and sees the run turn into `Cancelled` with its reason. Viewers see no cancel action. Cancelled runs show who cancelled them, when, and why.

**Why this priority**: Members manage the club through the Web UI; without it, cancellation is only reachable through the API.

**Independent Test**: In the simulated browser project and against the running platform, a Coach cancels a running run from the match page through the confirmation dialog with a reason and sees the cancelled state with the reason; a Viewer sees no action; a conflicting change shows the existing conflict message and the refreshed run.

**Acceptance Scenarios**:

1. **Given** a match page with runs, **When** a Club Admin or a Coach of the owning team views it, **Then** each run in `Requested` or `Running` offers a cancel action and runs in other states offer none; a Viewer never sees the action.
2. **Given** the cancel action, **When** the member chooses it, **Then** a confirmation dialog explains that work in progress is stopped and results already accepted remain, offers an optional reason field, and cancels only after confirmation; dismissing it changes nothing.
3. **Given** a confirmed cancellation, **When** the platform accepts it, **Then** the run list shows the run as `Cancelled` with the cancelling member, the cancellation time, and the reason, without a page reload.
4. **Given** a cancellation that the platform refuses because the run changed or ended meanwhile, **When** the refusal arrives, **Then** the Web UI shows the existing conflict or failure message, refreshes the run list, and offers the action again only if the run is still unfinished.
5. **Given** a cancelled run, **When** any member who may read the match views the match page, **Then** the run shows its cancelled state, the cancelling member, time, and reason.

### Edge Cases

- A run is cancelled at the same moment a Manager completes an attempt: exactly one of the two commits first; a completion committed first stays accepted and leads to no further readiness, and a completion arriving after the cancellation is refused as obsolete.
- Two members cancel the same run at the same time with the same version: one cancellation succeeds; the other receives the cancelled run as an idempotent success, never a second audit event or notification.
- A cancellation races with the run's own terminal transition (for example the last node completes): whichever commits first wins; the loser sees a conflict and the run keeps the winner's state.
- A run whose work messages remain in the work queue after cancellation: the messages are consumed and acknowledged as obsolete on claim and never start an attempt; the queue does not keep them indefinitely.
- A Manager that is offline during the cancellation: its attempt is fenced at once; when it reconnects, its calls are refused as obsolete and it cleans up; if it never returns, nothing of the run waits for it.
- A Manager that loses its registration while holding a fenced attempt of a cancelled run: the existing revocation behavior applies and the run is unaffected.
- The reason contains markup or is very long: it is stored and shown as plain text and bounded in length; an over-long reason is refused before anything changes.
- A cancelled run's waiting jobs never become ready, even if their upstream results were accepted before the cancellation.
- Cancelling a run does not affect other runs of the same match, including newer runs of the same or a later recording-set version.
- A member whose team role is revoked between viewing the match page and confirming is refused by the platform; the Web UI shows the refusal.

## Requirements *(mandatory)*

### Functional Requirements

#### Cancellation operation

- **FR-001**: The platform MUST offer a member-facing operation that cancels one analysis run, addressed by run, that requires the run's current version as a precondition and accepts an optional reason.
- **FR-002**: Only Club Admins and Coaches of the team that owns the run's match MUST be allowed to cancel; Viewers MUST be refused as forbidden, and members without a role on the owning team MUST receive a not-found answer that discloses nothing about the run.
- **FR-003**: The platform MUST allow cancellation only from `Requested` and `Running`; a cancellation of a run in `Completed`, `PartiallyCompleted`, or `Failed` MUST be refused with a conflict that names the terminal state, and a missing or outdated version MUST be refused with the existing precondition responses, without any change.
- **FR-004**: A cancellation request by an authorized member for a run that is already `Cancelled` MUST succeed without any change when it names the run's version from immediately before the cancellation or its current version, returning the cancelled run with its unchanged reason; any other version MUST be refused with the existing precondition response.
- **FR-005**: A successful cancellation MUST atomically set the run to `Cancelled`, set its end time to the cancellation time, record the cancelling member and the optional reason, increment the run's version, write one audit event with the member, run, team, match, and outcome, and enqueue one run-state-changed notification for the transition through the transactional outbox.
- **FR-006**: The reason MUST be optional, bounded in length, and stored and returned as plain text; invalid reasons MUST be refused before any change. The reason MUST NOT appear in the run-state-changed notification, which carries only the transition.
- **FR-007**: A `Cancelled` run MUST never transition to any other state, and none of its nodes or logical jobs may make further progress.

#### Stopping work and fencing

- **FR-008**: From the cancellation on, no work message of the run MUST be published or republished: pending ready-work publications of the run MUST be discarded or skipped, and reconciliation MUST NOT recreate them.
- **FR-009**: A claim for any logical job of a cancelled run MUST be refused as obsolete, so work messages that are already queued never start an attempt and are acknowledged by the Manager.
- **FR-010**: Every active attempt of the run MUST be closed in the cancellation's transaction so that its fencing token no longer authorizes anything; its later heartbeats, result-grant requests, upstream reads, and completions MUST be refused as obsolete, and no result of the run may be accepted after the cancellation commits.
- **FR-011**: The run's unfinished logical jobs (`Waiting`, `Ready`, `Claimed`) and workflow nodes (`Waiting`, `Active`) MUST end in a new terminal `Cancelled` state, distinct from `Failed` and `Blocked`; the run status read MUST report node state `Cancelled` and an additive `cancelled` job count per node; completed, failed, and blocked jobs and nodes keep their state.
- **FR-012**: Fenced attempts of a cancelled run MUST NOT consume or report attempt-budget exhaustion, MUST NOT requeue their jobs, and MUST NOT block descendants or change the outcome of any other run.
- **FR-013**: The Job Monitor, publication, reconciliation, readiness, and notification workers MUST leave cancelled runs, their jobs, and their attempts unchanged.
- **FR-014**: A Manager whose call for an attempt is refused as obsolete MUST stop the attempt's Analyst container through its runtime adapter, delete the attempt's staged inputs and result data, and report nothing for it, as best effort; platform correctness MUST NOT depend on this step.

#### Results and history

- **FR-015**: Results accepted before the cancellation MUST remain immutable, readable through the existing reads, and attributed to the cancelled run; cancellation MUST NOT delete, hide, or reattribute them.
- **FR-016**: The run reads (the run status and the match's run list) MUST expose, as additive fields for cancelled runs, the cancelling member's id and current account name (the name members know each other by, resolved when the run is read, never copied into the run), the cancellation reason, and the end time.

#### Web UI

- **FR-017**: The Web UI's match page MUST offer a cancel action for each run in `Requested` or `Running` to Club Admins and Coaches of the owning team only, and MUST NOT offer it for runs in other states or to Viewers; the platform's decision stays authoritative.
- **FR-018**: The cancel action MUST require confirmation in a dialog that states the consequences and offers an optional, length-bounded reason, and MUST send the run's version as the precondition.
- **FR-019**: After a cancellation, the run list MUST show the run as `Cancelled` with the cancelling member, time, and reason without a reload; a refused cancellation MUST show the existing conflict or failure messages and refresh the run list.
- **FR-020**: The Web UI MUST reach the operation only through an application-owned adapter over the generated client, render the reason as text only, use translation keys with English as the only shipped language, keep the dialog keyboard operable with visible focus and announced outcomes, and pass automated WCAG 2.2 AA rule checks; it stays online only and stores nothing about cancellations in the browser.

#### Evidence

- **FR-021**: Automated tests MUST cover the allowed and refused transitions, role-based access, version preconditions, idempotent repetition, concurrent cancellation and completion, discarded and refused publication and claims, fenced heartbeat, grant, upstream-read, and completion calls, immutable accepted results, worker behavior for cancelled runs, the Manager's best-effort cleanup with the simulated runtime, the Web UI action and dialog in the simulated and platform browser projects, and an end-to-end cancellation of a running run with an active attempt.

### Key Entities *(include if feature involves data)*

- **Analysis Run**: Gains its cancellation record: the cancelling member's id, the cancellation time (its end time), and the optional reason; the member's account name is resolved when read. A cancelled run is terminal.
- **Logical Job and Workflow Node**: Unfinished jobs and nodes of a cancelled run end in the terminal `Cancelled` state; finished ones keep theirs.
- **Execution Attempt**: Active attempts of a cancelled run are closed and fenced with a cancellation reason; they never become accepted.
- **Cancellation Audit Event**: The security audit record of a cancellation: member, run, team, match, outcome, and time.
- **Run-State-Changed Notification**: The existing lifecycle notification, carrying the transition to `Cancelled` without the reason.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Coach cancels a running run from the match page in at most 3 interactions (action, optional reason, confirm).
- **SC-002**: In automated tests, 100% of heartbeats, result-grant requests, upstream reads, and completions for attempts of a cancelled run made after the cancellation are refused, and 0 results of a cancelled run are accepted after the cancellation commits.
- **SC-003**: In automated tests, 0 work messages of a cancelled run are published after the cancellation, and 0 attempts are created from messages that were queued before it.
- **SC-004**: With the simulated runtime, a Manager holding an attempt of a cancelled run stops its container and removes its working data within two heartbeat intervals of the cancellation.
- **SC-005**: In automated tests, 100% of cancellation attempts by Viewers, members of other teams, with outdated versions, or for terminal runs are refused and leave the run unchanged, and repeated identical cancellations produce exactly one audit event and one notification.
- **SC-006**: Results accepted before a cancellation are byte-for-byte unchanged and readable after it in 100% of tested cases.

## Assumptions

- The reason is optional and limited to 500 characters of plain text; it is not translated and is shown as entered.
- The run's version for the precondition is the version returned by the run status read and the match's run list; both reads already report it.
- Managers already stop containers and clean up when a heartbeat or another call is refused as obsolete (Analyst Manager Work Execution and the Docker Desktop runtime adapter); the cancellation relies on that behavior and adds no new Manager command. The time until the container stops is bounded by the configured heartbeat interval, whose production value remains unresolved.
- Queued work messages are not removed from the work queue individually; they become obsolete through the claim refusal and are acknowledged by the Manager that pulls them.
- Production values for timeouts, heartbeat intervals, and queue retention stay unresolved and are not set by this feature.
- Out of scope: automatic supersession of older runs when a new recording-set version is finalized (the batch's next feature, Analysis Run Supersession, builds on this cancellation), rerunning or restarting runs, descendant-only recomputation, cross-run result reuse, cancelling single nodes or jobs, run controls in the Web UI's analysis review (Web UI Connected Analysis Review offers none), production timeout values, and notifying members by email about cancellations.
- Dependencies: the merged [Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md), [Analyst Manager Work Execution](../archive/20261012-141233-manager-work-execution/spec.md), [Analysis Result Manifests and Validated Acceptance](../archive/20261013-125502-analysis-result-manifests/spec.md), [Analysis Result Read for Jobs](../archive/20261013-134112-analysis-result-read/spec.md), [Docker Desktop Runtime Adapter for Analyst Execution](../archive/20261015-092159-docker-runtime-adapter/spec.md), and [Web UI Match Workflow and Recording Upload](../archive/20261013-112844-web-ui-match-upload/spec.md). The batch siblings [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md) and [Web UI Connected Analysis Review](../archive/20261017-181118-web-ui-analysis-review/spec.md) are independent of this feature: facts of a cancelled run stay readable like its other accepted results, and the review shows the cancelled state through the run read.
