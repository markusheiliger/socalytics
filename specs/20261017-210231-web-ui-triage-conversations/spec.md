# Feature Specification: Web UI Match Triage Conversations

**Feature Branch**: `20261017-210231-web-ui-triage-conversations`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Web UI Match Triage Conversations: spec 3 gives the platform triage conversations and direct agent invocations with conditional polling, but members have no screen for them; the architecture's Web UI lists agent conversations for coaches as not implemented (docs/architecture/client-applications.md#web-ui) and defines the client protocol (docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol, #post-analysis-match-triage). From a match whose analysis run is `Completed` or `PartiallyCompleted`, an authorized member starts a triage conversation for that run, adds participants from the stamp's enabled agents, and addresses a question to one participant; the Web UI submits with an idempotency key, shows the accepted (pending) turn at once, polls status conditionally with backoff until the invocation succeeds or fails, and renders the advice with clear attribution to the agent, the simulation/AI disclosure the agent catalog requires (docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance), uncertainty, and the evidence references and lineage the platform returns. Members list their conversations for a match, reopen one, and see the bound run and whether it is still the match's current run; Viewers see what spec 3 lets them read without submit controls; limits, refusals, scope loss, failures, unavailable evidence, and offline state are shown explicitly, and resubmission after a network failure reuses the idempotency key. Everything goes through application-owned adapters over the generated Kiota client, the existing navigation, i18n, accessibility, and online-only PWA rules (no conversation content cached by the service worker or stored locally). Out of scope: CA->SA consultations and Coach Agents, streaming responses, editing or deleting turns, sharing conversations, evidence clip playback (spec 10), offline mode, new API operations unless the plan proves a needed read is missing. Depends on specs/archive/20261017-204332-agent-orchestration-invocations (batch sibling), Web UI Foundation, Web UI Match Workflow and Recording Upload, Installable Web UI with Service Worker Updates (merged), and links to specs/20261017-181118-web-ui-analysis-review where the run is shown. Implements docs/architecture/client-applications.md#web-ui (agent conversations for coaches) and the Web UI side of docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol."

## Clarifications

### Session 2026-10-17

- Q: How should status checks slow down while a pending turn does not change? → A: Start with the platform's suggested interval, multiply it by 1.5 after each unchanged answer, cap it at 15 seconds, and reset it when the status changes.
- Q: Should the start action also be offered on the run review view? → A: No; conversations start only from the match view, and the conversation view links to the run's review.
- Q: How should long conversation histories load? → A: The first page of turns loads with the conversation; further pages load in submission order through a "show more" action and stay in memory for the open view only.
- Q: Should an open lineage of an unfinished turn keep updating? → A: Yes; while open, the lineage is refreshed whenever the turn's status check reports a change, and it stops with the turn.
- Q: What happens when the member leaves the conversation view with an unsent question or a submission whose outcome is unknown? → A: The Web UI asks for confirmation before leaving; pending turns need no confirmation because the platform keeps them.
- Q: How should browser tests prove the complete ask-and-answer flow, given that the platform's local test composition produces no `Completed` analysis run? → A: The complete flow, including polling, refusals, and failures, is proven against the simulated API; the platform browser test proves what the platform composition reaches (the triage section with its empty list and the no-eligible-run message, and the not-found view for an unknown conversation), and no test-only platform hook is added.

## User Scenarios & Testing *(mandatory)*

The actors are members of the team that owns an analysed match ([Terminology](../../docs/architecture/terminology-and-principles.md#terminology)).
The platform already owns triage conversations, direct `User -> SA` invocations, accepted advice, and lineage, with
its rules for who may create, submit, and read ([Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md));
this feature gives members the connected Web UI for them, as
[Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) and
[Connected Agent Conversations](../../docs/architecture/client-applications.md#connected-agent-conversations) require,
following [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)
and [Post-Analysis Match Triage](../../docs/architecture/intelligence-and-agents.md#post-analysis-match-triage). The
platform stays authoritative for every rule; the Web UI explains its answers.

### User Story 1 - Ask a specialist about an analysed match and read the advice (Priority: P1)

A Coach opens a match whose analysis has finished, starts a triage conversation for an analysis run that ended as
`Completed` or `PartiallyCompleted`, adds the tactics specialist, and asks it a question. The Web UI shows the question
at once as a pending turn, keeps its status current without the Coach doing anything, and, when the agent has answered,
shows the advice attributed to the agent and clearly disclosed as AI-generated.

**Why this priority**: It is the feature: without starting a conversation, asking, and reading the advice, members
cannot use agent triage at all.

**Independent Test**: As a Coach of the owning team, open a match with a `Completed` run, start a conversation for that
run, add `specialist-tactics`, submit a question, and confirm that the pending turn appears immediately, that its state
changes without a reload, and that the advice appears attributed to the tactics specialist with the AI disclosure.

**Acceptance Scenarios**:

1. **Given** a match with at least one `Completed` or `PartiallyCompleted` run, **When** a Club Admin or a Coach of the
   owning team opens the match, **Then** the match view offers to start a triage conversation and lets them choose one of
   those runs.
2. **Given** the start action, **When** the member confirms a run, **Then** a conversation bound to that match and run
   opens, showing the run and a link to its review.
3. **Given** an open conversation, **When** its owner adds an agent from the stamp's enabled agents, **Then** the agent
   appears as a participant with its name and domain and can be addressed.
4. **Given** a conversation with a participant, **When** its owner submits a question to that participant, **Then** the
   question appears at once as a pending turn addressed to that agent.
5. **Given** a pending turn, **When** the platform reports the invocation as succeeded, **Then** the turn shows the
   accepted advice attributed to the agent, with the disclosure that it is AI-generated advice for human review, without
   the member reloading.
6. **Given** a pending turn, **When** the platform reports the invocation as failed, **Then** the turn shows that no
   advice was produced and why (attempts exhausted, execution failed, or access lost).

---

### User Story 2 - Return to conversations and understand their context (Priority: P2)

A member returns to a match and finds the conversations they started there, newest first. They reopen one and see its
bound run, whether that run is still the match's current run, its participants, and its full history; they can open the
lineage of each answered question to see how the advice came about.

**Why this priority**: Triage happens over time; conversations must be found again and their context and lineage must be
understandable before advice can be trusted. It builds on Story 1.

**Independent Test**: Create two conversations for a match, the first for a run that a newer run later supersedes; return
to the match and confirm that both are listed newest first, that the first is marked as bound to a run that is no longer
current, and that its history and each turn's lineage are shown.

**Acceptance Scenarios**:

1. **Given** a member with conversations for a match, **When** they open the match, **Then** the match view lists their
   own conversations for that match, newest first, each with its bound run, its participants, and its creation time.
2. **Given** a listed conversation, **When** the member opens it, **Then** the history shows every question in submission
   order with its agent, its state, and its advice once succeeded.
3. **Given** a conversation whose run was superseded, **When** the member opens it, **Then** the view says that the run is
   no longer the match's current run and that the conversation stays bound to it, and the member can still ask questions.
4. **Given** an answered turn, **When** the member opens its lineage, **Then** the Web UI shows the platform's ordered
   lineage entries (submission, attempts, checkpoints, failures, and acceptance) with their times, and the declared
   execution references of the accepted advice.
5. **Given** a Viewer who owns a conversation from an earlier Coach role, **When** they open it, **Then** they can read
   it fully but see no start, add-participant, or submit controls, and the view says why.

---

### User Story 3 - Explicit limits, refusals, failures, and connectivity (Priority: P3)

When something goes wrong, the member always learns what happened and what they can do: a lost connection while sending
does not create a duplicate question when they send again, the per-owner limit of unfinished questions is explained,
refusals and lost access are named, and without a network the views say that a connection is required.

**Why this priority**: Agent work is asynchronous and can fail or be refused in many ways; silent failures or duplicate
questions would erode trust. The core flow works without it, but it must not ship without it.

**Independent Test**: Simulate a network failure on submit and confirm that sending again creates no second turn; submit
while the owner already has the maximum of unfinished questions and confirm the explanation; remove the member's team
role and confirm the not-found view; take the browser offline and confirm the connection-required state.

**Acceptance Scenarios**:

1. **Given** a submission whose answer was lost to a network failure, **When** the member sends the same question to the
   same agent again, **Then** the Web UI reuses the original request's idempotency key, and the platform's history shows
   one turn.
2. **Given** an owner who already has the configured number of unfinished questions, **When** they submit, **Then** the
   Web UI explains that too many questions are still being answered, keeps the question text, and lets them send it later.
3. **Given** a refusal (agent no longer available, agent not a participant, run not eligible, question invalid), **When**
   it is answered, **Then** the Web UI shows a specific message and keeps the member's input where it can be corrected.
4. **Given** a member who lost access to the team or whose conversation is not theirs, **When** they open or poll the
   conversation, **Then** the Web UI shows the not-found view and stops polling.
5. **Given** no network, **When** a member opens a conversation or the match's conversation list, **Then** the Web UI
   shows the connection-required state, offers no submission, and shows no conversation content from any local copy.

---

### Edge Cases

- **No eligible run**: A match without a `Completed` or `PartiallyCompleted` run offers no start action and says that a
  finished analysis is needed first.
- **Several eligible runs**: The member chooses the run; the newest eligible run is preselected.
- **Unchanged polls**: While nothing changes, the interval between status checks grows by a factor of 1.5 up to 15
  seconds and resets when the status changes; polling never creates another question.
- **Long history**: The first page of turns is shown with the conversation, and further pages are loaded on request in
  submission order without showing a turn twice.
- **Leaving with unsent input**: Leaving the conversation view with an unsent question or a submission whose outcome is
  unknown asks for confirmation first.
- **Hidden view or offline**: Polling pauses while the view is hidden or the browser is offline and resumes with an
  immediate check when it is visible and online again.
- **Several pending turns**: Each pending turn is tracked independently and ordered by submission time.
- **Agent disabled after being added**: The participant stays listed; submitting to it is refused with "agent not
  available", which the Web UI explains.
- **All enabled agents already participate**: The add action says that no further agents are available.
- **Concurrent change while adding a participant**: A conflict reloads the conversation and asks the member to choose
  again.
- **Scope lost during a pending turn**: The turn ends as failed with "access lost"; if the member also lost read access,
  the next poll answers not found and the not-found view is shown.
- **Very long advice**: Advice is shown in full as text with its line breaks preserved, never interpreted as markup.
- **Unknown values**: States, failure reasons, lineage kinds, and agents the Web UI does not know are shown with their raw
  value rather than hidden or failing.
- **No evidence references**: When the platform returns no evidence references for an advice, the Web UI says so
  explicitly instead of showing nothing.
- **Reload or sign-out with a pending turn**: Nothing is kept locally; after a reload the history from the platform shows
  the turn and polling resumes for it; sign-out discards the draft and any unsent question.

## Requirements *(mandatory)*

### Functional Requirements

#### Entry points and access

- **FR-001**: The match view MUST show a triage conversations section to members who may read the match; it MUST list the
  member's own conversations for that match, newest first, each with its bound run, participants, and creation time, and
  MUST say when there are none.
- **FR-002**: The match view MUST offer to start a triage conversation only to Club Admins and Coaches of the owning team,
  and only when the match has at least one `Completed` or `PartiallyCompleted` run; the member MUST choose one of those
  runs, with the newest preselected, and the platform's decision MUST stay authoritative.
- **FR-003**: Each conversation MUST be a distinct, addressable view of one conversation of one match; a conversation
  that does not exist, is not the member's, or belongs to another match MUST be shown as not found, without disclosing
  anything about it.
- **FR-004**: The conversation view MUST show its bound run with a link to that run's review view of
  [Web UI Connected Analysis Review](../20261017-181118-web-ui-analysis-review/spec.md), and MUST say when the run is no
  longer the match's current run and that the conversation stays bound to it. Conversations start only from the match
  view; the run review view offers no start action.
- **FR-004a**: The conversation view MUST show the first page of the history with the conversation and load further pages
  in submission order on the member's request, keeping loaded pages in memory for the open view only and showing each
  turn once.

#### Participants and questions

- **FR-005**: The owner MUST be able to add a participant chosen from the stamp's enabled agents that are not yet
  participants, each shown with a readable name, its identifier, and its domain; the add MUST be conditional on the
  conversation version last shown.
- **FR-006**: The owner MUST be able to write a plain-text question and address it to exactly one participant; the Web UI
  MUST prevent sending an empty question and MUST show the platform's refusals of invalid questions next to the input.
- **FR-007**: Each submission MUST carry an idempotency key created by the Web UI for that question to that agent; when the
  answer to a submission is lost to a network failure and the member sends the unchanged question to the same agent
  again, the Web UI MUST reuse its key, and it MUST create a new key once the question or the agent changes or the
  previous submission was answered.
- **FR-008**: On acceptance the Web UI MUST show the question at once as a pending turn, attributed to its agent, and MUST
  clear the input.
- **FR-009**: Viewers and other members without submit rights MUST see no start, add-participant, or submit controls, and
  the conversation view MUST say that it is read-only for them.

#### Status and advice

- **FR-010**: For every unfinished turn the Web UI MUST check the invocation status with conditional requests, starting
  with the platform's suggested interval and multiplying it by 1.5 after each unchanged answer up to 15 seconds,
  resetting it when the status changes; it MUST check only while the view is visible and the browser is online, and MUST stop once the
  invocation succeeded or failed.
- **FR-011**: When a turn ends, the Web UI MUST refresh the history so that the turn shows its final state and, once
  succeeded, its accepted advice.
- **FR-012**: Accepted advice MUST be shown in full as text, attributed to its agent by name and identifier, with a
  disclosure that it is AI-generated advice for a coach's review, not a decision or a statement by a person, as
  [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance)
  requires; the advice text, including its assessment, evidence, uncertainty, and recommendation parts, MUST be shown
  unaltered and in the agent's order.
- **FR-013**: For each advice the Web UI MUST show the evidence references the platform returns for it, and MUST say
  explicitly when there are none.
- **FR-014**: A failed turn MUST show that no advice was produced and a worded reason for attempts exhausted, execution
  failed, and access lost; other reasons MUST be shown with their raw value.
- **FR-015**: The owner MUST be able to open each turn's lineage and see its entries in the platform's order with kind,
  attempt number, and time, the bounded details the platform returns (checkpoint kind and note, failure reason and
  whether retryable), and the declared execution references of the accepted advice (runtime, model provider, model,
  deployment, and prompt, tool-policy, and safety-policy references). While an unfinished turn's lineage is open, it
  MUST be refreshed whenever the turn's status check reports a change, and stop refreshing with the turn.

#### Refusals, failures, and connectivity

- **FR-016**: The Web UI MUST show a specific message for each platform refusal of this feature's operations: too many
  unfinished questions, agent not available, agent not a participant, participant already present, run not eligible,
  invalid question, no submit right, and a stale conversation version; it MUST keep the member's input where it can be
  corrected or sent later.
- **FR-017**: When a submission's answer is lost to a network failure, the Web UI MUST say that it is unknown whether the
  question was received and offer to send it again (FR-007); when a later history read shows the turn, the Web UI MUST
  show it once.
- **FR-018**: A not-found answer to any conversation read or poll MUST show the not-found view and stop polling; an ended
  session MUST follow the Web UI's existing sign-in flow.
- **FR-019**: The triage views MUST be online only: conversation content, questions, advice, and lineage MUST NOT be cached
  by the service worker or stored in browser storage; drafts, idempotency keys, and pending state MUST live in memory
  only and be discarded on sign-out; without a network the views MUST show the connection-required state and offer no
  submission.
- **FR-019a**: Leaving the conversation view with an unsent question or a submission whose outcome is unknown MUST ask
  for confirmation first; pending turns MUST NOT require confirmation.

#### Interfaces and quality

- **FR-020**: The Web UI MUST reach the platform only through application-owned adapters over the generated client, using
  the member operations of [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md)
  and the existing match and analysis run reads; it MUST add no platform operation unless planning proves that a needed
  read is missing.
- **FR-021**: All new user-facing text MUST come from translation keys with English as the only shipped language; all new
  views MUST be operable by keyboard with visible focus, MUST announce turn state changes to assistive technology, and
  MUST pass automated WCAG 2.2 AA rule checks.
- **FR-022**: All platform-provided text (questions, advice, notes, reasons, identifiers, references) MUST be rendered as
  text, never as markup, keeping the Web UI's existing client security baseline.
- **FR-023**: The views MUST offer no streaming, editing, deletion, sharing, Coach Agents, consultations, evidence clip
  playback, or offline use.
- **FR-024**: Automated tests MUST cover the adapters' mapping, idempotency-key reuse, conditional polling with backoff,
  pause and resume, history paging, lineage refresh, the leave confirmation, every turn state and refusal, the
  superseded-run notice, read-only views for Viewers, role-based
  entry points, the not-found and connection-required states, the online-only rule, and accessibility rule checks; the
  complete ask-and-answer flow MUST be proven against the simulated API, and a browser test against the running platform
  MUST prove the triage section with its empty list and no-eligible-run message and the not-found view for an unknown
  conversation, without a test-only platform hook.

### Key Entities

- **Triage conversation view**: One member-owned conversation as the platform returns it: match, bound run and whether it
  is current, participants, version, and history.
- **Turn**: One question to one participant with its state (pending while queued or running, succeeded, failed), failure
  reason, and accepted advice once succeeded.
- **Question draft**: The member's unsent question and addressed agent with the idempotency key of its submission intent;
  held in memory only.
- **Advice presentation**: The accepted advice text with its agent attribution, AI disclosure, and evidence references.
- **Lineage view**: The ordered lineage entries and declared execution references of one invocation.
- **Enabled agent**: An agent the stamp enables, with identifier, group, domain, and the Web UI's readable name.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Coach can start a conversation from a match, add a specialist, and send a question in under 1 minute, and
  the pending turn appears within 1 second of the platform accepting it.
- **SC-002**: In the simulated-API browser tests, where the agent answers after a scripted delay, the advice appears in
  the conversation within 30 seconds of submission without a reload.
- **SC-003**: In automated tests, 0 resubmissions after a simulated network failure create a second turn, and 0 status
  checks happen while the view is hidden, offline, or after the turn ended.
- **SC-004**: In automated tests, every turn state, refusal, and failure reason of this feature shows its specific message,
  and 100% of advice shows its agent attribution and AI disclosure.
- **SC-005**: Automated accessibility checks report 0 WCAG 2.2 AA rule violations on the new views, and every flow is
  completed by keyboard alone.
- **SC-006**: In automated tests, 0 conversation contents, questions, advice texts, or lineage entries are found in the
  service worker caches or browser storage.

## Assumptions

- **Dependencies**: [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md)
  (batch sibling; conversations, participants, submission, private conditional reads, lineage, and the enabled-agent
  read; its own end-to-end tests prove the platform side with its simulated agent workload), [Web UI Foundation](../archive/20261012-150657-web-ui-foundation/spec.md)
  (sign-in, sessions, role navigation, adapters over the generated client, i18n, accessibility),
  [Web UI Match Workflow and Recording Upload](../archive/20261013-112844-web-ui-match-upload/spec.md) (the match view and
  its analysis run list), and [Installable Web UI with Service Worker Updates](../archive/20261015-194113-web-ui-pwa-updates/spec.md)
  (online-only shell, connection-required state, service worker rules). The run review view comes from
  [Web UI Connected Analysis Review](../20261017-181118-web-ui-analysis-review/spec.md); this feature only links to it.
- **Platform rules**: Who may create, submit, and read, the per-owner limit of unfinished questions, the question and
  advice length limits, and the suggested polling interval are the platform's; the Web UI does not duplicate them.
- **Evidence references**: The first platform slice returns no evidence references; they arrive with the MCP tools, so
  the Web UI shows their absence explicitly until then.
- **Readable agent names**: Readable names exist as translations for the catalog's identifiers; an identifier without a
  translation is shown as it is.
- **Out of scope**: `CA -> SA` consultations, Coach Agents, streaming, editing or deleting turns or conversations, removing
  participants, sharing, evidence clip playback, offline mode, new navigation entries outside the match view, and
  production values.
