---

description: "Task list for Web UI Match Triage Conversations"
---

# Tasks: Web UI Match Triage Conversations

**Input**: Design documents from `specs/20261017-210231-web-ui-triage-conversations/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. FR-024 asks for automated tests of the adapter, key reuse, polling with backoff, pause and resume, paging, lineage refresh, the leave confirmation, every state and refusal, the superseded-run notice, Viewer read-only views, role-based entry points, not-found and connection-required states, the online-only rule, accessibility, the complete flow against the simulated API, and a browser test against the running platform; test work is part of the tasks below.

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently. All paths are relative to the repository root; Web UI paths start with `src/clients/web/`.

**Before starting**: the batch sibling [Agent Orchestration Direct Invocations](../20261017-204332-agent-orchestration-invocations/spec.md) must be merged, so the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` contains its member operations; `npm run build` (or `npm run generate`) in `src/clients/web` regenerates the ignored Kiota client from it. This feature never changes the snapshot or any platform code.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files never overlap.
- **[Story]**: Which user story this task belongs to (US1, US2, US3).
- Every task names the files it creates or changes.
- Parallel tasks never edit `src/clients/web/src/i18n/en/triage.json` or `src/clients/web/src/i18n/index.ts`; they use the keys of T001, and T031 reconciles any gap.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The translation namespace and client models every view and the adapter use.

- [ ] T001 Create the `triage` translation namespace `src/clients/web/src/i18n/en/triage.json` with English texts for every label, heading, action, and message of `specs/20261017-210231-web-ui-triage-conversations/contracts/turn-states.md` and `contracts/routes-and-roles.md`: the match-view section (title, empty list, no eligible run, start action, run choice with state and creation time, "no longer current run" marker), the conversation view (bound run, review link, superseded notice, read-only note, back link), participants (add, none left, domain), readable names `agents.<agentId>` for `specialist-fitness`, `specialist-goalkeeping`, `specialist-tactics`, `specialist-video-analysis`, `specialist-scouting`, and `specialist-player-development`, the composer (question, agent, send, sending, send again, unknown outcome), turn states and failure reasons, the AI disclosure, the "no evidence references" note, lineage (show and hide, entry kinds, attempt, details, execution reference names), every refusal message, the live-region announcements, show more, and the leave confirmation; register the namespace in `ns` and `resources` of `src/clients/web/src/i18n/index.ts` (FR-012, FR-013, FR-021)
- [ ] T002 Create the application-owned models of `specs/20261017-210231-web-ui-triage-conversations/data-model.md` in `src/clients/web/src/platform/models/agents.ts`: `EnabledAgent`, `ConversationSummary`, `TriageConversation`, `Turn`, `TurnPage`, `Advice` (with `evidenceReferences` and optional `executionReferences`), `ExecutionReferences`, `InvocationStatus`, `InvocationLineage` with entries, `SubmissionAccepted`, `Conditional<T>`, the `TriageOutcome` union, and raw-value wrappers for state, failure reason, lineage kind, and group with a `known` flag, plus `isTurnPending(state)` (FR-014, FR-020)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The adapter, pure helpers, the draft hook, and the simulated API that every story builds on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Adapter, helpers, and simulated platform

- [ ] T003 [P] Create `src/clients/web/src/platform/adapters/agentConversationAdapter.ts` with every method of `specs/20261017-210231-web-ui-triage-conversations/contracts/agent-conversation-adapter.md` over the generated request builders (`withIdempotencyKey`, `withIfMatch`, `withJson` from `src/clients/web/src/platform/adapters/kiota.ts`), conditional reads through a Kiota response handler option that returns `unchanged` for `304`, parses `200` bodies with the generated model factories, and reads `ETag` and `Retry-After`, and the refusal translation table into a `TriageFailure` without changing `src/clients/web/src/platform/failure.ts`; add `src/clients/web/tests/unit/platform/adapters/agentConversationAdapter.test.ts` covering mapping of every response, unknown raw values, missing identifiers (`incompatible`), `304`, headers, every refusal row, and that no method touches browser storage (FR-003, FR-016, FR-020)
- [ ] T004 [P] Create `src/clients/web/src/features/triage/pollDelay.ts` with `initialPollDelay(retryAfterSeconds?)` (default 2 seconds) and `nextPollDelay(current, changed)` (×1.5 while unchanged, capped at 15 seconds, reset to the platform's interval on change); add `src/clients/web/tests/unit/features/triage/pollDelay.test.ts` (FR-010)
- [ ] T005 [P] Create `src/clients/web/src/features/triage/turnPresentation.ts` with `turnPresentation(turn)` and `triageMessage(outcome)` returning translation keys and parameters for every row of `specs/20261017-210231-web-ui-triage-conversations/contracts/turn-states.md` (raw values for unknown states, reasons, and lineage kinds) and `agentName(agentId, t)` with the identifier fallback; add `src/clients/web/tests/unit/features/triage/turnPresentation.test.ts` with one case per row (FR-014, FR-016)
- [ ] T006 [P] Create `src/clients/web/src/features/triage/useQuestionDraft.ts` holding text, addressed agent, idempotency key, and outcome in component state: the key is created with `crypto.randomUUID()` on the first send of an intent, reused for a resend of the unchanged text to the same agent after `unknownOutcome`, and dropped on a text or agent change or a definite answer; export the same rule as `useCreationIntent` for conversation creation; add `src/clients/web/tests/unit/features/triage/useQuestionDraft.test.ts` (FR-007, FR-017, FR-019)
- [ ] T007 [P] Create `src/clients/web/src/features/triage/triageKeys.ts` with the query keys `['triage', 'agents']`, `['triage', 'conversations', matchId]`, `['triage', 'conversation', conversationId]`, `['triage', 'turns', conversationId]`, `['triage', 'invocation', invocationId]`, and `['triage', 'lineage', invocationId]`; add `src/clients/web/tests/unit/features/triage/triageKeys.test.ts` (FR-020)
- [ ] T008 [P] Extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with seedable agent state and routes for every member operation of the sibling's API contract: enabled agents, conversations per owner and match (with `analysisRunIsCurrent`), participants with `version` and `412` on a stale `If-Match`, turns with cursor pages, invocation status that advances through scripted states with `ETag`, `304` on an unchanged `If-None-Match`, and `Retry-After`, lineage with entries and execution references, recorded idempotency keys with replay, test controls for each refusal (`429 too-many-unfinished-invocations`, `409` codes, `400` field errors, `403`, `404`), and a one-shot network failure after a submission was accepted (FR-024)

### Status polling

- [ ] T009 Create `src/clients/web/src/features/triage/useInvocationPolling.ts`: one timer per unfinished invocation using `getInvocationStatus` with the last `ETag`, delays from `pollDelay.ts`, pausing while `document.visibilityState` is `hidden` or `src/clients/web/src/connectivity/useConnectivity.ts` reports offline and checking at once on return, stopping on `succeeded`, `failed`, or `notFound`, invalidating `['triage', 'turns', conversationId]` on a terminal state, and exposing a change counter for open lineage panels; add `src/clients/web/tests/unit/features/triage/useInvocationPolling.test.ts` with fake timers for backoff, reset, pause and resume, stop, and no request after the end (FR-010, FR-011, FR-018, SC-003)

**Checkpoint**: Foundation ready; user story work can begin.

---

## Phase 3: User Story 1 - Ask a specialist about an analysed match and read the advice (Priority: P1) 🎯 MVP

**Goal**: An Editor starts a conversation from a match, adds a specialist, asks a question, sees the pending turn at once, and reads the attributed, disclosed advice without reloading.

**Independent Test**: In the simulated browser tests, a Coach starts a conversation for the newest eligible run, adds `specialist-tactics`, submits, sees the pending turn at once, and sees the advice with attribution and AI disclosure within 30 seconds; a failed turn shows its reason.

### View components

- [ ] T010 [P] [US1] Create `src/clients/web/src/features/triage/StartConversationDialog.tsx`: lists the match's `Completed` and `PartiallyCompleted` runs (state and creation time) with the newest preselected, creates the conversation with `useCreationIntent` and `createConversation`, and navigates to `/matches/:matchId/triage/:conversationId`; add `src/clients/web/tests/unit/features/triage/StartConversationDialog.test.tsx` (FR-002)
- [ ] T011 [P] [US1] Create `src/clients/web/src/features/triage/ParticipantsPanel.tsx`: participants with readable name, identifier, and domain, and for Editors an add control listing enabled agents (`listAgents`) not yet participating, adding with the shown `version` through `addParticipant`, and the "all agents participate" note; add `src/clients/web/tests/unit/features/triage/ParticipantsPanel.test.tsx` (FR-005)
- [ ] T012 [P] [US1] Create `src/clients/web/src/features/triage/AdviceCard.tsx`: agent name and identifier, the always-visible AI disclosure, the advice text unaltered as text with preserved line breaks (no markup rendering), and the evidence references or the explicit "no evidence references" note; add `src/clients/web/tests/unit/features/triage/AdviceCard.test.tsx` including markup-containing and long advice (FR-012, FR-013, FR-022)
- [ ] T013 [P] [US1] Create `src/clients/web/src/features/triage/QuestionComposer.tsx` for Editors: plain-text question, participant choice, send disabled for empty text, submission through `useQuestionDraft` and `submitQuestion`, "Sending…" while in flight, clearing on `202`, and an `onAccepted(SubmissionAccepted, agentId, question)` callback; add `src/clients/web/tests/unit/features/triage/QuestionComposer.test.tsx` (FR-006, FR-007, FR-008)

### History and pending turns

- [ ] T014 [US1] Create `src/clients/web/src/features/triage/TurnList.tsx`: the first history page (`listTurns`) in submission order, accepted pending turns kept in memory until a history page contains their `invocationId`, one `useInvocationPolling` per unfinished turn, the turn presentation of `turnPresentation.ts`, `AdviceCard` for succeeded turns, and polite live-region announcements of state changes; add `src/clients/web/tests/unit/features/triage/TurnList.test.tsx` (FR-008, FR-010, FR-011, FR-014, FR-021)

### Entry points and route

- [ ] T015 [P] [US1] Create `src/clients/web/src/features/triage/TriageSection.tsx` (the member's conversations for the match from `listConversations`, newest first, with links; for `canEditTeamMatches` the start action when an eligible run exists, otherwise the no-eligible-run note; the empty-list note) and render it after `AnalysisRunStates` in `src/clients/web/src/features/matches/MatchPage.tsx`; add `src/clients/web/tests/unit/features/triage/TriageSection.test.tsx` (FR-001, FR-002)
- [ ] T016 [P] [US1] Create the route view `src/clients/web/src/features/triage/ConversationPage.tsx` for `/matches/:matchId/triage/:conversationId` and register it in `src/clients/web/src/router.tsx`: load the match through `useMatchContext` (not-permitted view without a team role), load the conversation (`getConversation`; `notFound` or a different `matchId` → not-found view), show the bound run with a link to `/matches/:matchId/analysis-runs/:runId`, `ParticipantsPanel`, `TurnList`, and, for Editors, `QuestionComposer` whose accepted submissions feed the pending turns; add `src/clients/web/tests/unit/features/triage/ConversationPage.test.tsx` (FR-003, FR-004, FR-008)

### Browser evidence

- [ ] T017 [P] [US1] Create `src/clients/web/tests/e2e/simulated/triage.spec.ts` with an `Ask and answer` block: a Coach starts a conversation from the match for the preselected newest eligible run, adds `specialist-tactics`, submits, sees the pending turn within 1 second of the `202` and the advice with attribution and disclosure within 30 seconds without reload, the conditional status requests carry `If-None-Match` and their intervals grow while unchanged, and each failure reason (attempts exhausted, execution failed, access lost) shows its message (SC-001, SC-002, SC-004)
- [ ] T018 [P] [US1] Create `src/clients/web/tests/e2e/platform/triage.spec.ts` (signs in as the Club Admin, opens the match the upload flow finalized, checks the triage section with its empty list and the no-eligible-run message, and the not-found view for an unknown conversation address) and add a chained `platform-triage` project to `src/clients/web/playwright.config.ts` directly before the final `platform` project: it depends on the project that precedes `platform` at implementation time, and `platform` then depends on `platform-triage`, so `--project platform` still runs the whole chain (FR-024)

**Checkpoint**: User Story 1 is functional and testable independently (MVP).

---

## Phase 4: User Story 2 - Return to conversations and understand their context (Priority: P2)

**Goal**: Members find and reopen their conversations, see the bound run's status, page through long histories, open each turn's lineage, and Viewers read without controls.

**Independent Test**: In the simulated browser tests, two conversations are listed newest first, the superseded one shows its notice, "Show more" loads further turns without duplicates, a lineage panel shows ordered entries and execution references and refreshes while its turn changes, and a Viewer reads their conversation without controls.

### Context components

- [ ] T019 [P] [US2] Create `src/clients/web/src/features/triage/LineagePanel.tsx`: loads `getLineage` on first open, shows entries in sequence order with worded or raw kind, attempt number, time, and details as text, and the advice's execution references by name; refetches conditionally when the given change counter changes and stops once the turn ended; add `src/clients/web/tests/unit/features/triage/LineagePanel.test.tsx` (FR-015, FR-022)
- [ ] T020 [P] [US2] Extend `src/clients/web/src/features/triage/TriageSection.tsx` so each listed conversation shows its bound run, participants, creation time, and the "no longer current run" marker; extend `src/clients/web/tests/unit/features/triage/TriageSection.test.tsx` (FR-001)
- [ ] T021 [P] [US2] Extend `src/clients/web/src/features/triage/ConversationPage.tsx` with the superseded-run notice when `analysisRunIsCurrent` is false (questions stay possible) and the read-only note for members without submit rights (no add control, no composer); extend `src/clients/web/tests/unit/features/triage/ConversationPage.test.tsx` (FR-004, FR-009)

### Paging and lineage in the history

- [ ] T022 [US2] Extend `src/clients/web/src/features/triage/TurnList.tsx` with history paging (`useInfiniteQuery` over the cursor, "Show more" in submission order, each turn once across pages and pending turns, pages in memory only) and a "Show lineage" toggle per turn that mounts `LineagePanel` with the turn's polling change counter; extend `src/clients/web/tests/unit/features/triage/TurnList.test.tsx` (FR-004a, FR-015)
- [ ] T023 [US2] Add a `Return and context` block to `src/clients/web/tests/e2e/simulated/triage.spec.ts`: conversations listed newest first with run, participants, and marker; the superseded notice; three history pages with "Show more" and no duplicate turn; a lineage panel with ordered entries and execution references that refreshes while its turn runs; a Viewer who owns a conversation reads it without start, add, or submit controls and sees the read-only note (FR-024)

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Explicit limits, refusals, failures, and connectivity (Priority: P3)

**Goal**: Every refusal, lost answer, lost access, and offline situation is explained, a resend never duplicates a question, unsent input is protected, and nothing is stored locally.

**Independent Test**: In the simulated browser tests, a submission whose answer is lost is sent again with the same key and leaves one turn; each refusal shows its message and keeps the draft; a revoked owner sees the not-found view and polling stops; offline shows the connection-required state; leaving with unsent input asks first; axe, keyboard, and storage checks pass.

### Refusal handling in the controls

- [ ] T024 [P] [US3] Extend `src/clients/web/src/features/triage/QuestionComposer.tsx` with refusal handling: `tooManyUnfinished`, `agentNotAvailable`, `agentNotParticipant`, and `invalidQuestion` messages next to the input with the draft kept, the unknown-outcome message with "Send again" reusing the key, and `notPermitted` reported to the page; extend `src/clients/web/tests/unit/features/triage/QuestionComposer.test.tsx` (FR-016, FR-017)
- [ ] T025 [P] [US3] Extend `src/clients/web/src/features/triage/ParticipantsPanel.tsx` so `participantExists`, `staleVersion`, and `agentNotAvailable` reload the conversation and ask the member to choose again; extend `src/clients/web/tests/unit/features/triage/ParticipantsPanel.test.tsx` (FR-016)
- [ ] T026 [P] [US3] Extend `src/clients/web/src/features/triage/StartConversationDialog.tsx` with the `runNotEligible` and `notPermitted` messages and the unknown-outcome "Try again" that reuses the creation key; extend `src/clients/web/tests/unit/features/triage/StartConversationDialog.test.tsx` (FR-016, FR-017)

### Page-level failures, connectivity, and leaving

- [ ] T027 [US3] Extend `src/clients/web/src/features/triage/ConversationPage.tsx` and `src/clients/web/src/features/triage/TriageSection.tsx`: `notFound` from any read or poll shows the not-found view and stops all polling; `notPermitted` on submission switches to read-only; offline shows the existing connection-required state and disables every state-changing control in both views; leaving with an unsent draft or an unknown-outcome submission asks for confirmation through `useLeaveGuard` from `src/clients/web/src/uploads/useLeaveGuard.ts` and a React Router `useBlocker`; extend `src/clients/web/tests/unit/features/triage/ConversationPage.test.tsx` and `TriageSection.test.tsx` (FR-018, FR-019, FR-019a)
- [ ] T028 [US3] Add a `Failures and refusals` block to `src/clients/web/tests/e2e/simulated/triage.spec.ts`: a one-shot network failure after acceptance followed by "Send again" leaves one turn and one idempotency key at the simulated API; each refusal of `contracts/turn-states.md` shows its message with the draft kept; a stale participant add reloads; revoking the owner while a turn is pending shows the not-found view and no further status request; the leave confirmation appears for unsent input and not for pending turns (SC-003, SC-004)

### Access, accessibility, and online-only evidence

- [ ] T029 [P] [US3] Extend `src/clients/web/tests/unit/serviceWorker/shellWorker.test.ts` to assert that `classifyRequest` leaves `/api/v1/agent-catalog/agents`, `/api/v1/matches/{id}/agent-conversations`, `/api/v1/agent-conversations/{id}` with its `participants`, `invocations`, and `turns` paths, and `/api/v1/agent-invocations/{id}` with its `lineage` path unhandled for every method (FR-019)
- [ ] T030 [P] [US3] Create `src/clients/web/tests/e2e/simulated/triage-access.spec.ts`: the start action for a Club Admin and a Coach and not for a Viewer; the not-permitted or not-found view for members without a team role or without ownership; axe checks (`tests/e2e/support/axe.ts`) on the match section and the conversation view in the pending, succeeded, failed, refused, read-only, superseded, lineage-open, and connection-required states; the whole flow by keyboard with visible focus; offline shows the connection-required state without earlier conversation content; after a session, Cache Storage has no `/api/` entry and `localStorage`, `sessionStorage`, and IndexedDB hold no question, advice, or lineage text; where these checks fail, fix focus order, labels, live regions, and connectivity handling in `src/clients/web/src/features/triage/` view components in the same task (FR-002, FR-009, FR-019, FR-021, SC-005, SC-006)

**Checkpoint**: All user stories work independently and meet the Web UI's quality rules.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Translation consistency, documentation, and the full validation.

### Translations and documentation

- [ ] T031 [P] Reconcile `src/clients/web/src/i18n/en/triage.json` with the keys the triage components and `turnPresentation.ts` use (no missing or unused keys) and make `src/clients/web/tests/unit/i18n/keys.test.ts` pass with no literal user-facing text in `src/clients/web/src/features/triage/` (FR-021)
- [ ] T032 [P] Make `docs/architecture/` describe the merged state as `plan.md` → Architecture Updates requires: re-read `docs/architecture/` and correct every narrative and status statement this feature affects, for example the triage paragraph, the "agent conversations for coaches (… not implemented yet …)" bullet, and the retry and polling sentence in `docs/architecture/client-applications.md` → Web UI and Connected Agent Conversations, "Web UI agent workflows remain future implementation work" in `docs/architecture/platform-implementation.md` → Agent Runtime, the Web UI evidence and "the other Web UI client workflows (… agent conversations)" in its Acceptance Evidence and Architecture Reassessment, and "user experiences are likewise design inputs" in `docs/architecture/intelligence-agent-catalog.md`; the listed rewrites are examples, not the whole job, and planned, future, or not-implemented wording about the Web UI's triage conversations must not remain while Coach Agents and consultations stay explicitly unimplemented; update `AGENTS.md` ("Current State"), `src/clients/README.md`, and the root `README.md` where they describe the Web UI's workflows; run `node .github/scripts/check-markdown.mjs`

### Validation

- [ ] T033 Run the validation of `specs/20261017-210231-web-ui-triage-conversations/quickstart.md` from `src/clients/web` (`npm ci`, `npm run build`, `npm test`, `npm run test:e2e -- --project simulated`, `npm run test:e2e -- --project platform`) and fix any failure in the files of this feature

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: needs the merged batch sibling (agent operations in the OpenAPI snapshot); T001 and T002 run in order.
- **Foundational (Phase 2)**: depends on Setup; T003–T008 form one parallel group; T009 follows (it uses T003 and T004).
- **User Story 1 (Phase 3)**: depends on Foundational.
- **User Story 2 (Phase 4)**: depends on US1 (it extends `TriageSection.tsx`, `ConversationPage.tsx`, and `TurnList.tsx`).
- **User Story 3 (Phase 5)**: depends on US1 and US2 (it extends their components and covers every view and state).
- **Polish (Phase 6)**: depends on all stories; T033 runs last.

### User Story Dependencies

- **US1 (P1)**: independent after Foundational; the MVP.
- **US2 (P2)**: builds on the US1 views; independently testable through its own browser block.
- **US3 (P3)**: hardens all views; independently testable through its browser blocks and the access spec.

### Within Each User Story

- Components before the views that compose them; unit tests in the same task as the code they cover.
- Browser evidence after the views it exercises.
- `src/clients/web/tests/e2e/simulated/triage.spec.ts` is created by T017 and extended by T023 and T028 in later phases, never in parallel.

### Parallel Opportunities

- Phase 2: T003–T008 (adapter, helpers, draft hook, query keys, simulated API; disjoint files).
- Phase 3: T010–T013 (separate components and tests); T015 and T016 (`TriageSection.tsx` with `MatchPage.tsx`; `ConversationPage.tsx` with `router.tsx`); T017 and T018 (simulated spec; platform spec and `playwright.config.ts`).
- Phase 4: T019–T021 (`LineagePanel.tsx`, `TriageSection.tsx`, `ConversationPage.tsx` with their own tests).
- Phase 5: T024–T026 (composer, participants, start dialog); T029 and T030 (the service-worker unit test; the access spec with any view fixes it requires).
- Phase 6: T031 and T032.

---

## Parallel Example: Foundational

```text
Task: "T003 agentConversationAdapter.ts with its tests"
Task: "T004 pollDelay.ts with its tests"
Task: "T005 turnPresentation.ts with its tests"
Task: "T006 useQuestionDraft.ts with its tests"
Task: "T007 triageKeys.ts with its tests"
Task: "T008 agent routes in simulatedApi.ts"
```

## Parallel Example: User Story 1

```text
Task: "T010 StartConversationDialog.tsx with tests"
Task: "T011 ParticipantsPanel.tsx with tests"
Task: "T012 AdviceCard.tsx with tests"
Task: "T013 QuestionComposer.tsx with tests"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2.
2. Complete Phase 3 (US1): start, add, ask, pending turn, polling, and advice from the match.
3. Validate with T017 and T018.

### Incremental Delivery

1. US1 → members can triage a match with a specialist.
2. US2 → conversations are found again with context, paging, and lineage; Viewers read without controls.
3. US3 → every refusal, lost answer, and connectivity state is explicit and proven accessible and online only.
4. Polish → translations, architecture, and full validation.
