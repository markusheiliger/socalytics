# Feature Specification: Web UI Request of a New Analysis Run

**Feature Branch**: `20261023-072900-web-ui-request-analysis-run`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Give Club Admins and Coaches of the owning team a connected Web UI action on the match page to request a new analysis run for a match whose recording set is finalized, so that a coach can repeat analysis after the stamp's default workflow, an approved Analyst profile, or an approved image changed, without uploading a recording again. The Web UI calls the member operation that the spec 20261023-015644-explicit-analysis-rerun adds under /api/v1 and shows the outcome in the match page's analysis run list. The action is confirmed, explains the platform's refusals without deciding any rule in the client, follows the existing role gating of the cancel action, hides for Viewers, is online only, and keeps no run data or idempotency key beyond memory. Tested by the simulated and platform browser-test projects."

## User Scenarios & Testing *(mandatory)*

The actors are Club Admins of the club that owns a match and Coaches of the team that owns it. The platform will let
them request an analysis run explicitly
([Explicit Analysis Run Request](../20261023-015644-explicit-analysis-rerun/spec.md)); until the Web UI offers it, the
operation is reachable only by calling the API by hand. This feature adds the action to the analysis run list of the
match page ([Web UI](../../docs/architecture/client-applications.md#web-ui),
[Run state and recovery](../../docs/architecture/job-processing.md#run-state-and-recovery)). It changes no platform
operation, contract, or rule: the platform decides what is allowed, creates the run, and supersedes older runs; the Web
UI only asks and tells the outcome.

### User Story 1 - Request a new analysis run from the match page (Priority: P1)

A Club Admin or Coach opens a match whose recording set is finalized and finds a "Request new analysis" action with the
match's analysis run list. Choosing it opens a confirmation that states the outcome before anything happens: the
platform starts a new run of the stamp's current default workflow on the match's current finalized recordings, without
uploading again; if a run for the same recordings and the same resolved workflow version already exists, no duplicate is
created and that run is shown instead; and a newly created run becomes the current run and marks older runs as
superseded. After the member confirms, the run list shows the outcome without a manual reload.

**Why this priority**: It is the whole value of the feature: a coach repeats analysis after the default workflow, an
approved Analyst profile, or an approved image changed, without calling the API by hand or uploading again.

**Independent Test**: With a match that has a finalized recording set and an earlier run, change the default workflow
(or have none of the existing runs match it), sign in as a Coach of the owning team, request a new analysis run, confirm,
and see the new run in the run list as the current run and the earlier run as superseded.

**Acceptance Scenarios**:

1. **Given** a signed-in Club Admin of the match's club or Coach of the owning team and a match with a finalized
   recording set, **When** the match page is shown, **Then** the analysis run list offers the "Request new analysis"
   action, even when the match has no run yet.
2. **Given** the action, **When** the member chooses it, **Then** a confirmation opens that states in plain language what
   will happen (a new run of the stamp's current default workflow on the current finalized recordings; no new upload; a
   duplicate is never created; a new run becomes the current run and supersedes older ones) and offers confirm and cancel;
   nothing is sent before the member confirms, and cancelling or dismissing the confirmation sends nothing.
3. **Given** the member confirms, **When** the platform accepts the request and reports that it created a run, **Then**
   the confirmation reports success in text, the run list is read again, the new run appears marked as the current run, and
   older runs appear marked as superseded by it, with no manual reload.
4. **Given** the member confirms, **When** the platform accepts the request and reports that a run already exists for the
   same recordings and workflow version, **Then** the outcome states that no new run was created because this analysis
   already exists, links to that existing run in the list, and, when the platform names a different current run, says so
   without deciding which run is current itself.
5. **Given** the request was accepted, **When** the outcome is shown, **Then** it offers the existing way to open the
   run's review, and the run's progress is shown by the run list as it is for any other run.
6. **Given** a request in flight, **When** the member looks at the action, **Then** it shows a pending state and cannot be
   activated again, so one confirmation sends one request.

---

### User Story 2 - Understand why a request is refused (Priority: P1)

When the platform refuses a request, the member sees a plain-language explanation of the reason the platform reported and
what they can do about it, never a bare error and never a decision made by the client.

**Why this priority**: Refusals are expected (nothing finalized, no usable default workflow, role lost, already
analysed); a coach who cannot tell a lost request from an impossible one will retry blindly or ask an administrator.

**Independent Test**: Provoke each refusal (a match whose recordings are not finalized, a stamp with no resolvable default
workflow, a member whose role was revoked after the page loaded, a season that was archived, a temporarily unavailable
dependency) and see a distinct, accurate message each time, with the run list unchanged and no run created.

**Acceptance Scenarios**:

1. **Given** the platform refuses because the match has no finalized recording set, **When** the refusal arrives, **Then**
   the member is told that analysis needs a finalized recording set and that the recordings must be uploaded and finalized
   first, and no run appears.
2. **Given** the platform refuses because the stamp's default workflow cannot be resolved, **When** the refusal arrives,
   **Then** the member is told that no usable default workflow is configured and that a Club Admin must resolve this in
   the Analyst Registry administration, naming the reported category in words without inventing detail.
3. **Given** the platform refuses as forbidden or as not found (the role was lost or the match is no longer visible),
   **When** the refusal arrives, **Then** the member is told they are not allowed to request analysis for this match or
   that it is no longer available, the match data is read again so the page reflects their current access, and the action
   disappears when access no longer permits it.
4. **Given** the platform refuses because the season is archived, **When** the refusal arrives, **Then** the member is told
   that changes are no longer possible for an archived season.
5. **Given** the platform answers that a dependency is temporarily unavailable, **When** the answer arrives, **Then** the
   member is told it is temporary and may repeat the request; repeating it reuses the request's identity so a request that
   did reach the platform is not duplicated.
6. **Given** the session ended or expired, **When** the platform answers as unauthenticated, **Then** the member is taken
   through the Web UI's existing sign-in handling and no run data stays visible.
7. **Given** any refusal, **When** the member looks at the run list, **Then** it is unchanged by the request, the member
   can close the explanation and continue, and the client never pre-judges a refusal by its own reading of recordings,
   workflows, or runs.

---

### User Story 3 - Only the right members see the action, and only while online (Priority: P2)

The action follows the same role gating as the existing cancel action of the run list: Club Admins of the club and Coaches
of the owning team see it; Analysts, Viewers, members of other teams, and Club Admins of other clubs do not. It is not
shown while the platform cannot be reached.

**Why this priority**: It keeps the interface honest about what the member may do without moving any authorization
decision into the client; the platform stays the authority.

**Independent Test**: Open the same match as Club Admin, Coach, Viewer, and a member of another team: only the first two
see the action; disconnect and see that no request can be sent and that nothing is queued.

**Acceptance Scenarios**:

1. **Given** a Viewer of the owning team (or any member other than a Club Admin of the club or a Coach of the owning
   team), **When** the match page is shown, **Then** the action is not shown and the Web UI sends no request for it.
2. **Given** a match whose recording set is not finalized, **When** an eligible member opens the match page, **Then** the
   run list states in text that analysis needs a finalized recording set and the action is disabled with that reason
   rather than hidden (so the member learns why), where the Web UI already knows from the match's recording data that no
   finalized recording set exists; the platform's refusal still decides if the Web UI is wrong.
3. **Given** the Web UI cannot reach the platform, **When** the member tries to confirm, **Then** the member is told that
   requesting analysis needs a connection, nothing is queued for later, and no local analysis data is kept.
4. **Given** the member's role changes while the page is open, **When** the next read or refusal reflects it, **Then** the
   action appears or disappears accordingly without a client-side decision about the role.

---

### User Story 4 - Repeat, double-submit, and concurrent requests stay safe (Priority: P3)

A member who confirms twice, whose request is retried after a connection hiccup, or who requests while a colleague does the
same, never ends up with duplicate runs and always sees a consistent outcome.

**Why this priority**: The platform guarantees one run per recording-set version and resolved workflow version; the Web UI
must not undermine it by sending uncorrelated requests.

**Independent Test**: Confirm, lose the connection before the answer arrives, repeat the request, and see exactly one run
and the same outcome; have two members confirm at the same time and see one new run and one "already exists" outcome.

**Acceptance Scenarios**:

1. **Given** a confirmation, **When** the Web UI sends the request, **Then** it sends one idempotency key generated for
   that confirmation and keeps it in memory only; a repeat of the same confirmation after a transient failure sends the
   same key; a later, separate confirmation sends a new key.
2. **Given** the platform answers a repeated request with the originally recorded answer, **When** it is shown, **Then** it
   is presented like the original outcome, with no claim that a second run was created.
3. **Given** a refusal recorded by the platform for a key, **When** the member tries again after fixing the cause (for
   example a Club Admin set a default workflow), **Then** the Web UI uses a new key for the new confirmation so the retry is
   evaluated afresh.
4. **Given** the platform answers that a key was reused in an incompatible way, **When** that arrives, **Then** the member
   is told the request could not be processed and may start a new request, which uses a new key.
5. **Given** the member leaves the page or reloads while a request is in flight, **When** they return, **Then** the run list
   shows what the platform now has; the Web UI keeps no request, key, or run data beyond the page's memory.

---

### Edge Cases

- The default workflow changed back so that the platform returns an existing superseded run: the outcome says the run
  already exists and, when the platform names a different current run, which one is current; nothing is marked by the client.
- The run list is long or paged: the new run is found through the platform's read, wherever it appears in the platform's
  order, and the outcome links to it.
- The request succeeded but the following run list read fails: the outcome still reports the acceptance, the list shows its
  existing unavailable state with a retry, and no second request is sent.
- A run is returned in a terminal or running state: the outcome reports the run's state as the platform reports it.
- The member opens the confirmation and the match meanwhile gains a newer recording-set version or the workflow changes: the
  platform evaluates at confirmation time; the confirmation text never promises which workflow version will be used.
- Two actions open at once (cancel and request on different runs): each follows its own confirmation and pending state; one
  never answers for the other.
- A match with no runs but a finalized recording set (for example after runs were never created): the empty run list offers
  the action.
- Keyboard and screen-reader use: the action, the confirmation, the pending state, and the outcome are reachable, announced,
  and not conveyed by colour alone, like the existing cancel action.
- Switching language: confirmation and outcome texts are available in every supported language of the Web UI.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The match page's analysis run list MUST offer a "Request new analysis" action to Club Admins of the club and
  Coaches of the owning team, using the same role gating as the cancel action of the run list, including when the match has
  no run yet.
- **FR-002**: The action MUST NOT be shown to Viewers, Analysts, members of other teams, or Club Admins of other clubs, and the
  Web UI MUST NOT send the request for them; the platform's refusal remains the authority.
- **FR-003**: Choosing the action MUST open a confirmation that states in plain language that a new run of the stamp's current
  default workflow on the match's current finalized recordings will be requested, that no recording is uploaded again, that no
  duplicate run is created when the same analysis already exists, and that a newly created run becomes the current run and
  supersedes older runs; the confirmation MUST NOT name a workflow or version it cannot know.
- **FR-004**: Nothing MUST be sent before the member confirms; cancelling or dismissing the confirmation MUST send nothing and
  MUST leave the page as it was.
- **FR-005**: On confirmation the Web UI MUST call the existing member operation for requesting an analysis run through the
  generated client, with no body and no choice of workflow, profile, image, or version, one request per confirmation.
- **FR-006**: While a request is in flight the action MUST show a pending state and MUST NOT be activatable again.
- **FR-007**: The Web UI MUST send an idempotency key generated for each confirmation and keep it in memory only; a repeat of
  the same confirmation after a transient failure MUST reuse its key, and a later separate confirmation MUST use a new key.
- **FR-008**: When the platform reports that a run was created, the Web UI MUST say so, read the run list again, and show the
  new run as the current run and older runs as superseded exactly as the platform's run read reports them, with no manual
  reload and no client-side computation of currency or supersession.
- **FR-009**: When the platform reports that the run already exists, the Web UI MUST say that no new run was created, identify
  and link the existing run, and, when the platform names a different current run, state that too; it MUST NOT create or
  mark anything itself.
- **FR-010**: The Web UI MUST explain each refusal the platform reports in plain language with a next step, distinguishing at
  least: no finalized recording set; default workflow not resolvable (with the platform's category in words); forbidden; not
  found; archived season; temporary dependency failure; reused idempotency key; unauthenticated; and any other failure
  generically. The explanation MUST be derived from the platform's problem result, and the Web UI MUST NOT decide any
  eligibility, resolution, deduplication, supersession, or authorization rule itself.
- **FR-011**: After a forbidden or not-found refusal the Web UI MUST read the match and its run list again so that the action
  and data reflect the member's current access.
- **FR-012**: A temporary dependency failure MUST allow repeating the request with the same idempotency key; a new
  confirmation after any other refusal MUST use a new key.
- **FR-013**: Where the Web UI already knows from its match data that the match has no finalized recording set, the action
  MUST be disabled with that reason stated in text; the platform's answer MUST remain decisive when the data is out of date.
- **FR-014**: The feature MUST be online only: it MUST NOT queue, defer, or retry requests in the background, and when the
  platform cannot be reached it MUST say that requesting analysis needs a connection.
- **FR-015**: The Web UI MUST keep no run data, request, outcome, or idempotency key in durable storage, the service worker
  cache, or any store beyond the page's memory, and MUST NOT log them to the console or analytics.
- **FR-016**: After an accepted request, the outcome MUST offer the existing way to open the run's review, and the run's
  progress MUST be shown by the existing run list behavior.
- **FR-017**: The action, confirmation, pending state, outcomes, and refusal explanations MUST be keyboard operable, announced
  to assistive technology, not conveyed by colour alone, available in every supported language, and follow the Web UI's
  existing patterns for confirmations, accessibility, session handling, and memory-only data.
- **FR-018**: The feature MUST be covered by tests in the `simulated` browser-test project (against the fake platform, for
  every outcome and refusal above, role gating, pending state, key handling, and absence of persisted data) and in the
  `platform` browser-test project (against the real platform, for the created, already-exists, and at least the
  no-finalized-recording-set and unresolvable-workflow outcomes, and role gating); unit tests MUST cover the outcome and
  refusal mapping.
- **FR-019**: The feature MUST NOT add or change any platform operation, contract, or rule, and MUST NOT add
  descendant-only recomputation, per-request workflow or version choice, scheduled or bulk requests, other run controls,
  visualizations, comparison, export, or offline behavior.
- **FR-020**: Implementing the feature MUST correct the statements of the architecture that it makes false: the Web UI's
  statement that the analysis review offers no run controls and the description of the match page's run list in
  [Web UI](../../docs/architecture/client-applications.md#web-ui), and the run state and recovery description in
  [Run state and recovery](../../docs/architecture/job-processing.md#run-state-and-recovery) and
  [Durable workflow model](../../docs/architecture/job-processing.md#durable-workflow-model), so that they describe the
  implemented action.

### Key Entities *(include if feature involves data)*

- **Analysis run request**: One confirmed member intent to analyse a match again; identified for its lifetime in the page by an
  in-memory idempotency key; has no state of its own beyond pending, accepted, or refused.
- **Request outcome**: What the platform answered: a created run, an existing run (with the current run when different), or a
  refusal with its reason; shown to the member and never stored.
- **Analysis run**: Existing entity shown in the match page's run list with its state, current and superseded markers, and
  cancel action; unchanged by this feature.
- **Match recording data**: The existing finalized-or-not information of the match page, used only to explain why the action
  is disabled.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Coach can request a new analysis run for a match with a finalized recording set in at most three
  interactions (choose, read the confirmation, confirm), with the outcome visible in the run list without a manual reload.
- **SC-002**: In 100% of tested outcomes for a successful request, the run list shows the run the platform returned, with the
  current and superseded markers exactly as the platform's reads report them.
- **SC-003**: 100% of tested refusals (no finalized recording set, no resolvable default workflow, forbidden, not found,
  archived season, dependency unavailable, reused key, unauthenticated) show a distinct plain-language explanation and leave
  the run list unchanged.
- **SC-004**: 0 duplicate runs result from tested double activations, repeats after transient failures, and concurrent
  requests by two members.
- **SC-005**: The action is shown to 100% of Club Admins of the club and Coaches of the owning team and to 0% of Viewers,
  Analysts, and members without a role on the owning team, in every tested case.
- **SC-006**: After a tested session of requests, 0 bytes of run data, outcomes, or idempotency keys are present in browser
  storage or caches.
- **SC-007**: Every state of the action (available, disabled with reason, pending, confirmation, each outcome and refusal) is
  reachable by keyboard and exposes its meaning to assistive technology in the browser-test projects.

## Assumptions

- The member operation, its `202` answer (run identity, whether it was created, state, the current run when different, and the
  status link), its refusals, and its idempotency-key behavior are as defined by
  [Explicit Analysis Run Request](../20261023-015644-explicit-analysis-rerun/spec.md) (issue #242), which is not yet
  implemented; this feature MUST NOT start before it is implemented, and its spec twin dependency names that feature. The
  committed OpenAPI snapshot and the generated client are refreshed by that feature, so this feature adds no API change.
- Other prerequisites are implemented: the Web UI foundation, the match page with its run list and cancel action
  ([Web UI Match Workflow and Recording Upload](../archive/20261013-112844-web-ui-match-upload/spec.md)), member-initiated
  [Analysis Run Cancellation](../archive/20261017-182600-analysis-run-cancellation/spec.md), and
  [Analysis Run Supersession](../archive/20261017-183953-analysis-run-supersession/spec.md), whose current and superseded
  markers this feature relies on.
- The roles that may request analysis are those the platform authorizes for writes on the owning team's matches (Club Admin of
  the club, Coach of the owning team), the same set as for cancellation; the Web UI mirrors it only to hide the action.
- Confirmation, accessibility, internationalization, session handling, and memory-only data follow the Web UI's existing
  patterns; the idempotency key is a fresh random value per confirmation and is never shown or persisted.
- Out of scope: descendant-only recomputation, per-request workflow or version choice, scheduled or bulk requests, other run
  controls, visualizations, comparison, export, offline behavior, and any new API operation.
- No production values, retention, or rate limits are introduced; those remain unresolved decisions of the platform.
