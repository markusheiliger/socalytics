# Feature Specification: Analysis Run Supersession

**Feature Branch**: `20261017-183953-analysis-run-supersession`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Analysis Run Supersession: a Coach may finalize a new recording-set version for a match (correcting or adding recordings), and the Durable Analysis Workflow then creates a new analysis run for it; today the earlier run of the previous version keeps running and both stay equal in every read, because the durable workflow deferred supersession together with cancellation. The architecture accepts that any changed source input creates an entirely new run while prior runs and accepted results remain immutable history (docs/architecture/job-processing.md#run-state-and-recovery), and that supersession applies only across runs (docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access), but it does not yet state what happens to an older run that is still active, nor how reads present superseded runs; this spec decides it and its plan updates those narratives. Behaviour: when the run for a newer finalized recording-set version of a match is created, every older run of that match that is still `Requested` or `Running` is cancelled automatically with the reason `superseded` by the system (no member actor), through spec 3's cancellation mechanics (fencing, revocation, Manager best-effort cleanup, audit, events), and records the superseding run; older runs that are already terminal are not changed in state but are marked as superseded by the newer run in reads. Run reads (`getAnalysisRun`, `listMatchAnalysisRuns`) expose superseded-by information and identify the match's current run (the newest run of the latest finalized recording-set version); accepted results of superseded runs remain readable history and are never mixed into the current run. Supersession is idempotent under duplicate or out-of-order recordings-finalized events and concurrent member cancellation, never supersedes a run by an older version, and never touches runs of other matches. The Web UI's match run list marks superseded runs and their superseding run. Out of scope: rerunning the same recording set, choosing which run is current by hand, descendant-only recomputation, cross-run result reuse, deleting superseded results (retention stays governed elsewhere), the analysis review's own presentation beyond what spec 2 already reads. Depends on `specs/20261017-182600-analysis-run-cancellation` (batch sibling), Durable Analysis Workflow, Recording Lineage and Upload, Web UI Match Workflow and Recording Upload (merged; archived under specs/archive/). Implements and updates docs/architecture/job-processing.md#run-state-and-recovery, docs/architecture/match-data-pipeline.md (recording-set revision), and docs/architecture/client-applications.md#web-ui."

## Clarifications

### Session 2026-10-17

- Q: Should recording the supersession of a run that had already ended write an audit event or a run-state-changed notification? → A: Neither; the run's state does not change, so only the immutable supersession record is written and the reads show it.
- Q: Should the match page hide superseded runs by default? → A: No; every run stays listed as today, with current and superseded markers.
- Q: Should the platform count supersessions in its analysis metrics? → A: Yes; a supersession counter in the existing analysis metrics, split by whether the superseded run was active (and cancelled) or had already ended.
- Q: When the analysis reconciliation supersedes a run that predates this feature, which run is recorded as its superseding run? → A: The match's current run at the time of the reconciliation, the same rule as for a run created already superseded.

## User Scenarios & Testing *(mandatory)*

The actors are Coaches and Club Admins, who finalize recording-set versions of their team's matches, and every member who may read a match (Club Admins, and Coaches and Viewers of the owning team). The platform side extends the run semantics of [Run State And Recovery](../../docs/architecture/job-processing.md#run-state-and-recovery) for a revised recording set as described in [Upload](../../docs/architecture/match-data-pipeline.md#upload), and keeps the cross-run boundary of [Capability-Scoped Result Access](../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access). Stopping an active run reuses the cancellation of [Analysis Run Cancellation](../20261017-182600-analysis-run-cancellation/spec.md); the Web UI side extends the match page's run list of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui).

### User Story 1 - A newer recording set stops the outdated analysis (Priority: P1)

A Coach finalized a match's recordings, analysis started, and then the Coach noticed a missing half and finalized a corrected recording set. When the platform creates the analysis run for the corrected set, it stops every older run of the match that is still requested or running: the older run becomes cancelled because it was superseded, with no member named as its canceller, and it records which run superseded it. Its queued and active work stops exactly as for a member's cancellation, and results it accepted before stay readable history.

**Why this priority**: Without it, outdated analysis keeps consuming Analyst Manager capacity and produces results for recordings the team has already corrected; it is the core of supersession.

**Independent Test**: With the simulated runtime, a match has a running run for recording-set version A with one active attempt and further ready jobs; a Coach finalizes version B; once B's run exists, A's run is `Cancelled` with cause superseded and superseding run B, no member is recorded as canceller, A's ready work is never claimed, A's active attempt is refused as obsolete on its next call, the Manager stops and cleans up, A's previously accepted results are unchanged, and exactly one audit event and one run-state-changed notification exist for A's transition.

**Acceptance Scenarios**:

1. **Given** a match with a `Requested` or `Running` run for an older recording-set version, **When** the run for a newer finalized recording-set version of the same match is created, **Then** in the same atomic step the older run becomes `Cancelled` with cause superseded, its end time is the supersession time, no member is recorded as canceller, and the newer run is recorded as its superseding run.
2. **Given** an older run superseded while it was active, **When** its work is inspected after the supersession, **Then** its unfinished nodes and logical jobs are `Cancelled`, its active attempts are closed and fenced, its pending work publications are discarded, and later claims, heartbeats, result-grant requests, upstream reads, and completions for it are refused as obsolete, exactly as for a member's cancellation.
3. **Given** a superseded run, **When** the audit records and run-state-changed notifications are inspected, **Then** exactly one audit event attributed to the system names the run, its team and match, the superseding run, and the outcome, and exactly one run-state-changed notification announces the transition to `Cancelled`.
4. **Given** results the older run accepted before the supersession, **When** members or jobs read them, **Then** they are unchanged, remain attributed to the older run, and never appear in the newer run's reads or inputs.
5. **Given** a newer run that was itself created as `Failed` because its workflow could not be validated, **When** it is created, **Then** it still supersedes the older active runs of the match, because their source input is outdated.
6. **Given** a match whose older run is active and another match of the same team with an active run, **When** the newer run of the first match is created, **Then** the other match's run is unchanged.

---

### User Story 2 - Reads tell the current run from superseded history (Priority: P1)

A Viewer opens the runs of a match through the API and sees, for every run, whether it is the match's current run and, for a superseded run, which run superseded it and when. Runs that had already completed, partially completed, failed, or been cancelled by a member when the newer run appeared keep their state and their results, but are marked as superseded by the newer run. Reading a superseded run's results stays possible and never returns results of another run.

**Why this priority**: Members and later features (analysis review, agents) need one unambiguous answer to "which analysis describes this match now" without losing history; it is independently useful for terminal runs even without stopping active ones.

**Independent Test**: For a match with a completed run of version A and a newer completed run of version B, the run read of A and the match's run list report A as superseded by B with the supersession time and not current, B as current and not superseded; A's state, version, end time, and results are unchanged; a match with one run reports it as current; a match without runs reports no current run.

**Acceptance Scenarios**:

1. **Given** an older run that was already `Completed`, `PartiallyCompleted`, `Failed`, or `Cancelled` by a member when a newer version's run was created, **When** it is read, **Then** its state, end time, failure or cancellation details, version, and accepted results are unchanged, it reports the newer run as its superseding run and the supersession time, and no audit event or run-state-changed notification was written for it.
2. **Given** a match with runs, **When** a member reads the match's run list or one run, **Then** each run states whether it is the match's current run, exactly one run of the match is current when the match has a run, and the current run is the newest run of the latest finalized recording-set version that has a run.
3. **Given** a run that is not superseded, **When** it is read, **Then** it reports no superseding run.
4. **Given** a superseded run, **When** a member reads its results or facts through the existing reads, **Then** the reads return only that run's accepted results, never results of the superseding run or any other run.
5. **Given** a member without a role on the owning team, **When** the member reads a run, **Then** the existing not-found answer applies and nothing about supersession is disclosed.

---

### User Story 3 - Supersession stays correct under duplicates, reordering, and races (Priority: P1)

Recordings-finalized events may be delivered more than once or out of order, two recording sets of a match may be finalized almost at once, and a member may cancel the older run at the same moment the platform supersedes it. In every case each run is superseded at most once, only by a run of a newer recording-set version of the same match, and the record of who stopped an active run is consistent.

**Why this priority**: The work queue delivers at least once and messages can be reordered; without these guarantees supersession could stop the wrong run or produce duplicate audit events and notifications.

**Independent Test**: Integration tests deliver the recordings-finalized events of versions A, B, and C of one match in the orders A-B-C, C-B-A, and B-A-C, with duplicates, and concurrently with a member cancellation of A; in every case the runs of A and B end superseded, C's run is current, each superseded run records exactly one superseding run of a newer version, and each transition produced exactly one audit event and one notification.

**Acceptance Scenarios**:

1. **Given** a recordings-finalized event that was already processed, **When** it is delivered again, **Then** no run is created, superseded, or changed again and no further audit event or notification appears.
2. **Given** a run of a newer recording-set version that already exists, **When** the run of an older version of the same match is created later, **Then** the older run is created already superseded by the match's current run and `Cancelled` with cause superseded in the same atomic step, and none of its work is ever published.
3. **Given** a run that is already superseded, **When** a run of an even newer version is created, **Then** the run keeps its first superseding run and its state; only runs that are not yet superseded are superseded by the new run.
4. **Given** a member cancellation and a supersession of the same active run at the same moment, **When** both are processed, **Then** whichever commits first decides the run's cancellation record: a member's cancellation that commits first stays a member cancellation and the run is additionally marked superseded; a supersession that commits first stays a superseded cancellation and the member's request is answered as defined by the cancellation feature for an already cancelled run; never both a member and a superseded cancellation, and never a second audit event or notification for the transition.
5. **Given** two runs of the same recording-set version, **When** either is created, **Then** neither supersedes the other, because supersession applies only across recording-set versions.
6. **Given** runs that existed before this feature and are older than another run of the same match, **When** the platform's analysis reconciliation runs, **Then** they are superseded by the same rules, and repeating the reconciliation changes nothing further.

---

### User Story 4 - See superseded and current runs on the match page (Priority: P2)

On the match page, a Coach sees the finalized recording sets with their runs as today; the current run is marked as current, and every superseded run is marked as superseded with a reference to the run and recording set that superseded it and when. A run stopped because it was superseded reads as superseded, not as cancelled by a member, and names no member.

**Why this priority**: Members use the Web UI; without the markers, a cancelled older run looks like a member's decision and members cannot see which analysis is current.

**Independent Test**: In the simulated browser project and against the running platform, a Coach finalizes a second recording set while the first run is running and sees the first run turn into a superseded run that references the second set's run, and the second run marked as current; a Viewer sees the same markers.

**Acceptance Scenarios**:

1. **Given** a match page with several runs, **When** any member who may read the match views it, **Then** exactly the current run is marked as current and each superseded run is marked as superseded with the finalization time of the superseding run's recording set and the supersession time.
2. **Given** a run cancelled because it was superseded, **When** it is shown, **Then** it reads as superseded, shows no cancelling member and no cancel action, and is distinguishable from a member's cancellation.
3. **Given** a run cancelled by a member and later superseded, **When** it is shown, **Then** it shows both the member's cancellation details and the superseded marker.
4. **Given** a match page open while a newer run supersedes an older one, **When** the run list refreshes as it does today for unfinished runs, **Then** the markers update without a page reload.

### Edge Cases

- A newer recording-set version is finalized but its run cannot be created yet (for example no resolvable workflow): no older run is superseded until the newer run exists, and the older run stays current.
- A newer recording-set version's notification is rejected (for example a lineage mismatch) and never creates a run: no run is superseded by it.
- Two recording-set versions of a match carry the same finalization time: the deterministic tie-break of the version order decides which is newer, identically in every supersession and read.
- The superseding run is later cancelled by a member, fails, or is itself superseded: the runs it superseded stay superseded by it; supersession is never undone.
- A completion of an older run's attempt commits just before the supersession: the result stays accepted and leads to no further readiness; a completion arriving after it is refused as obsolete.
- A superseded run's queued work messages are consumed and acknowledged as obsolete on claim, never starting an attempt.
- A Manager that is offline during the supersession: its attempt is fenced at once; when it reconnects its calls are refused as obsolete and it cleans up.
- A member tries to cancel a run that is already cancelled because it was superseded: the cancellation feature's rules for an already cancelled run apply, and the run's cancellation cause stays superseded.
- A match with many superseded runs: supersession and reads stay correct for every run and list them like other runs.
- A run created already superseded (older version arriving late) never becomes current, never publishes work, and is announced with one transition notification.

## Requirements *(mandatory)*

### Functional Requirements

#### Version order and supersession

- **FR-001**: The platform MUST order the finalized recording-set versions of a match by finalization time with a deterministic tie-break, and MUST use this one order for every supersession decision and every current-run answer.
- **FR-002**: When the platform creates an analysis run for a recording-set version, in the same atomic step it MUST supersede every run of the same match that is not yet superseded and whose recording-set version is older than the version of the match's current run, and MUST record for each the match's current run as the superseding run (the new run itself whenever it belongs to the latest version) and the supersession time.
- **FR-003**: A run of a newer recording-set version MUST supersede older runs regardless of the newer run's own state, including a newer run created as `Failed`.
- **FR-004**: The platform MUST NOT supersede a run by a run of the same or an older recording-set version, MUST NOT supersede runs of other matches, and MUST NOT supersede anything on behalf of a recording-set version that has no run.
- **FR-005**: A run MUST be superseded at most once; its first superseding run and supersession time are immutable and are never changed or removed, even when the superseding run later fails, is cancelled, or is itself superseded.
- **FR-006**: When a run is created for a recording-set version older than the version of an existing run of the same match, the platform MUST create it already superseded by the match's current run and `Cancelled` with cause superseded in the same atomic step, and MUST never publish any of its work.

#### Stopping superseded active runs

- **FR-007**: A superseded run that is `Requested` or `Running` MUST be cancelled in the supersession's atomic step with the same effects on nodes, logical jobs, attempts, pending work publications, claims, heartbeats, result-grant requests, upstream reads, completions, workers, and Manager cleanup as a member's cancellation defined by the Analysis Run Cancellation feature.
- **FR-008**: A cancellation by supersession MUST record the cause superseded and no cancelling member and no free-text reason, MUST set the run's end time to the supersession time, MUST write one audit event attributed to the system that names the run, team, match, superseding run, and outcome, and MUST enqueue one run-state-changed notification for the transition through the transactional outbox, using the existing notification contract unchanged.
- **FR-009**: A member cancellation MUST record the cause member, so that every cancelled run states whether a member or supersession cancelled it.
- **FR-010**: A superseded run that is already `Completed`, `PartiallyCompleted`, `Failed`, or `Cancelled` MUST keep its state, end time, failure or cancellation details, version, and accepted results; recording its supersession MUST NOT change the run's version, so that the cancellation feature's idempotency rules stay valid, and MUST write neither an audit event nor a run-state-changed notification, because the run's state does not change.
- **FR-011**: Supersession and a concurrent member cancellation of the same run MUST be serialized so that exactly one cancellation record, one audit event for the transition, and one run-state-changed notification result; a supersession of a run that a member cancelled first MUST only record the supersession.

#### Idempotency and recovery

- **FR-012**: Duplicate and out-of-order recordings-finalized events MUST lead to the same final supersession records and run states as in-order delivery, and repeated processing MUST produce no further changes, audit events, or notifications.
- **FR-013**: Run creations of the same match MUST be serialized with each other so that concurrent finalizations cannot leave an older run unsuperseded or supersede a newer run.
- **FR-014**: The platform's existing analysis reconciliation MUST also apply the supersession rules to runs of matches that have a run of a newer recording-set version but no supersession record, including runs created before this feature, MUST record the match's current run at that time as their superseding run, and MUST be idempotent.
- **FR-014a**: The platform MUST count supersessions in its existing analysis metrics, distinguishing superseded runs that were active (and therefore cancelled) from runs that had already ended.

#### Reads

- **FR-015**: The run reads (the run status and the match's run list) MUST expose, as additive fields, whether the run is the match's current run, its superseding run (run and recording-set version) and supersession time when superseded, and for cancelled runs the cancellation cause (member or superseded).
- **FR-016**: The current run of a match MUST be the newest run of the latest finalized recording-set version that has a run; a match with runs MUST have exactly one current run, and a match without runs none.
- **FR-017**: Accepted results, facts, and manifests of superseded runs MUST remain readable through the existing reads with the existing authorization, MUST remain attributed to their run, and MUST never be returned by reads of another run or staged as inputs of another run.
- **FR-018**: Supersession information MUST be disclosed only to members who may read the run; members without a role on the owning team MUST receive the existing not-found answer.

#### Web UI

- **FR-019**: The Web UI's match run list MUST keep listing every run as today and MUST mark the current run as current and every superseded run as superseded, naming the superseding run's recording set by its finalization time and the supersession time, for every member who may read the match; superseded runs MUST NOT be hidden or collapsed.
- **FR-020**: A run cancelled by supersession MUST be shown as superseded without a cancelling member, and a run cancelled by a member MUST keep its member cancellation details; a member-cancelled run that was later superseded MUST show both.
- **FR-021**: The Web UI MUST reach the new read fields only through the application-owned adapter over the generated client, use translation keys with English as the only shipped language, keep the markers perceivable without color alone and announced through the run list's existing status semantics, pass automated WCAG 2.2 AA rule checks, and store nothing about supersession in the browser.

#### Evidence

- **FR-022**: Automated tests MUST cover supersession of active and terminal runs, the cancellation effects on work and attempts, the system audit event and notification, the version order and tie-break, out-of-order and duplicate events, concurrent finalizations, concurrent member cancellation, runs of the same version and of other matches, reconciliation of pre-existing runs, the additive read fields and current-run answer, the supersession metric, unchanged readable results of superseded runs, the Web UI markers in the simulated and platform browser projects, and an end-to-end supersession of a running run with an active simulated attempt.

### Key Entities *(include if feature involves data)*

- **Analysis Run**: Gains whether it is the match's current run (derived when read) and, for cancelled runs, the cancellation cause (member or superseded).
- **Run Supersession**: The immutable record that one run was superseded by a run of a newer recording-set version of the same match: superseded run, superseding run, and supersession time. At most one per run; it does not change the superseded run's version.
- **Recording-Set Version Order**: The per-match order of finalized recording-set versions by finalization time with a deterministic tie-break, used for supersession and the current run.
- **Supersession Audit Event**: The system-attributed audit record of a cancellation by supersession.
- **Run-State-Changed Notification**: The existing lifecycle notification, carrying the transition to `Cancelled` unchanged.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests, 100% of older active runs of a match are `Cancelled` with cause superseded in the same atomic step that creates the newer version's run, and 0 runs of other matches or of the same recording-set version change.
- **SC-002**: In automated tests, 0 work messages of a superseded run are published after the supersession, 0 attempts start from its queued messages, and 0 of its results are accepted after the supersession commits.
- **SC-003**: For every tested delivery order of three recording-set versions, including duplicates and a concurrent member cancellation, the final run states and supersession records are identical to in-order delivery, with exactly one audit event and one notification per transition.
- **SC-004**: In 100% of tested reads, a match with runs reports exactly one current run, and every superseded run names its superseding run.
- **SC-005**: Results accepted by superseded runs are byte-for-byte unchanged and readable after the supersession in 100% of tested cases.
- **SC-006**: A member viewing the match page identifies the current run and each superseded run's successor without opening another page.
- **SC-007**: With the simulated runtime, a Manager holding an attempt of a superseded run stops its container and removes its working data within two heartbeat intervals of the supersession.

## Assumptions

- The version order uses the finalization time recorded with each recording-set version; ties are broken by the version's identity, which the platform assigns in time order. No new member-visible version number is introduced.
- Runs are created only from recordings-finalized events (one per recording-set version and workflow); rerunning a recording set, changing the current run by hand, and creating runs on Registry changes stay out of scope.
- Supersession reuses the Analysis Run Cancellation feature's cancellation effects, enforcement, and Manager behavior; it adds no Manager change and no new Manager command.
- The run-state-changed notification contract stays unchanged; consumers learn the cause and the superseding run from the run reads.
- The analysis review of Web UI Connected Analysis Review reads the run as it does; presenting supersession there beyond the existing run state is out of scope.
- Retention of superseded runs' results stays governed by the existing retention rules; this feature deletes nothing.
- Production values for timeouts and heartbeat intervals remain unresolved and are not set by this feature.
- Out of scope: rerunning the same recording set, choosing the current run by hand, descendant-only recomputation, cross-run result reuse, deleting superseded results, comparing runs, notifying members by email about supersession, and presenting supersession in the analysis review beyond the run state it already reads.
- Dependencies: the batch sibling [Analysis Run Cancellation](../20261017-182600-analysis-run-cancellation/spec.md) (cancellation effects, `Cancelled` node and job states, fencing, Manager cleanup, cancellation read fields, and the cancel action in the run list), and the merged [Durable Analysis Workflow](../archive/20261005-130703-durable-analysis-workflow/spec.md), [Recording Lineage and Upload](../archive/20261005-130702-recording-lineage-upload/spec.md), and [Web UI Match Workflow and Recording Upload](../archive/20261013-112844-web-ui-match-upload/spec.md). The batch siblings [Accepted Fact Indexing and Capability-Scoped Fact Queries](../archive/20261017-175648-fact-indexing-queries/spec.md) and [Web UI Connected Analysis Review](../archive/20261017-181118-web-ui-analysis-review/spec.md) are unaffected: their reads never cross runs, so facts of superseded runs stay readable as history.
