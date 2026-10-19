# Research: Web UI Match Triage Conversations

All decisions build on the member operations of
[Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/contracts/agent-orchestration-api.md)
and the existing Web UI patterns in `src/clients/web/`. No `NEEDS CLARIFICATION` remains.

## R1 - Routes and entry points

- **Decision**: The match view (`src/features/matches/MatchPage.tsx`) gains a `TriageSection` after the analysis run
  list: the member's own conversations for the match (`listAgentConversations`), newest first, and, for Club Admins and
  Coaches of the owning team (`canEditTeamMatches`), a start action that opens a dialog listing the match's
  `Completed` and `PartiallyCompleted` runs from the existing run list read (`listMatchAnalysisRuns`), newest preselected.
  Confirming calls `createAgentConversation` and navigates to the new route `/matches/:matchId/triage/:conversationId`.
  The route view loads the match through `useMatchContext`, shows `NotPermittedPage` when the member cannot read the
  match, and the not-found view when the conversation read answers `404` or its `matchId` differs from the route.
- **Rationale**: FR-001 to FR-004; the clarification keeps the start action on the match view only; nesting the route
  under the match keeps the existing match context, access checks, and back navigation, as the run review route
  `/matches/:matchId/analysis-runs/:runId` of [Web UI Connected Analysis Review](../archive/20261017-181118-web-ui-analysis-review/plan.md) does.
- **Alternatives considered**: A top-level `/agent-conversations/:id` route (rejected: loses the match context and adds
  a navigation entry outside the match view, which the spec excludes); a start action on the run review view (rejected
  by clarification).

## R2 - Adapter and models

- **Decision**: A new `src/platform/adapters/agentConversationAdapter.ts` exposes `listAgents`, `listConversations`,
  `createConversation`, `getConversation`, `addParticipant`, `submitQuestion`, `listTurns`, `getInvocationStatus`, and
  `getLineage` over the generated request builders and maps every response into application-owned models in
  `src/platform/models/agents.ts` (data-model.md). Unknown states, failure reasons, and lineage kinds are kept as raw
  strings with a known/unknown flag. Refusals are translated into typed triage outcomes inside the adapter
  (`too-many-unfinished-invocations`, `agent-not-available`, `agent-not-participant`, `participant-exists`,
  `analysis-run-not-eligible`, field violations, `403`, `404`, `412`) from the `PlatformFailure`'s status and code,
  without changing the shared `mapProblem`.
- **Rationale**: FR-020 and the Web UI rule that screens reach the platform only through application-owned adapters;
  the shared mapping maps `429` with an unknown code to `incompatible`, so the triage outcome must be derived locally, as
  the analysis review does for its refusals.
- **Alternatives considered**: Extending `mapProblem` with agent codes (rejected: changes the shared failure kinds every
  feature depends on).

## R3 - Conditional reads and `304`

- **Decision**: `getConversation`, `listTurns`, `getInvocationStatus`, and `getLineage` accept the previous `ETag` and
  send it as `If-None-Match`. They attach Kiota's response handler option with a small conditional handler that
  returns `unchanged` for `304`, parses `200` bodies with the generated model factory through the request adapter's
  parse node factory, and captures `ETag` and `Retry-After`; any other status goes through the shared problem mapping.
- **Rationale**: Kiota treats every non-2xx status, including `304`, as an error and the existing adapters never read
  response headers; the conditional handler keeps the generated request builders, middleware (connectivity, session
  outcome, minimum version, anti-forgery), and models while making the protocol's unchanged answer and retry guidance
  visible (FR-010, [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)).
- **Alternatives considered**: Unconditional polling (rejected: the architecture requires conditional polling);
  mapping the `304` error after the fact (rejected: loses the headers and abuses the failure path).

## R4 - Idempotency keys and the question draft

- **Decision**: A `useQuestionDraft` hook holds the draft text, the addressed agent, and the idempotency key of the
  current submission intent in component state. The key is created with `crypto.randomUUID()` when the first submission
  of an intent is sent, is reused when the same text to the same agent is sent again after an unknown outcome
  (`unreachable`), and is dropped when the text or agent changes or a definite answer arrives (`202` or a refusal).
  Conversation creation uses the same rule for its dialog. Keys and drafts never leave memory; sign-out clears them with
  the session's existing `queryClient.clear()` and unmount.
- **Rationale**: FR-007, FR-017, FR-019; `withIdempotencyKey` already leaves key ownership with the caller so a retry of
  the same intent reuses it.
- **Alternatives considered**: Persisting the key in session storage to survive reloads (rejected: FR-019 forbids
  browser storage; after a reload the platform's history shows whether the turn exists).

## R5 - Status polling with backoff

- **Decision**: A `useInvocationPolling` hook per unfinished turn runs a timer, not TanStack's fixed `refetchInterval`:
  the first delay is the submission's `retryAfterSeconds` (or the read's `Retry-After`, default 2 seconds), each
  `unchanged` answer multiplies it by 1.5 up to 15 seconds, and a changed answer resets it. It pauses while
  `document.visibilityState` is `hidden` or the connectivity context reports offline and checks at once on return; it
  stops when the invocation is `succeeded` or `failed` or the read answers `404`. A pure `nextPollDelay` function holds
  the arithmetic and is unit-tested. On a terminal state the hook invalidates the turns query (FR-011); on any change
  it signals an open lineage to refresh (FR-015).
- **Rationale**: FR-010 and the clarified backoff; 15 seconds keeps SC-002's 30-second bound with the platform's
  2-second development interval.
- **Alternatives considered**: Polling the whole turns page (rejected: heavier and not the invocation status location
  the protocol returns); server push (out of scope).

## R6 - History paging and pending turns

- **Decision**: Turns load through `useInfiniteQuery` with the platform's cursor: the first page with the conversation,
  further pages through "Show more". Accepted submissions are added to an in-memory pending list keyed by
  `invocationId` and rendered after the loaded history until a refreshed history page contains the same
  `invocationId`, so each turn is shown once. On a terminal state the loaded pages are refetched.
- **Rationale**: FR-004a, FR-008, FR-011, and the clarified paging.
- **Alternatives considered**: Loading every page at once (rejected by clarification).

## R7 - Advice presentation, disclosure, and evidence references

- **Decision**: `AdviceCard` renders the advice as one text block with preserved line breaks (`white-space: pre-wrap`),
  never as markup, headed by the agent's readable name and identifier and an always-visible disclosure ("AI-generated
  advice for your review; it is not a decision and not the statement of a person"). It shows "No evidence references
  were recorded for this advice" because the member reads of the first platform slice return no evidence references;
  the adapter models an empty `evidenceReferences` list so a later platform field needs only an adapter change. The
  advice's own assessment, evidence, uncertainty, and recommendation parts stay in the agent's text and order.
- **Rationale**: FR-012, FR-013, FR-022, and
  [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance);
  the first catalog wave has only Specialist Agents, so the named-Coach-Agent simulation disclosure is not needed yet.
- **Alternatives considered**: Rendering Markdown in advice (rejected: FR-022 and the Trusted Types baseline); parsing
  the response contract's parts out of the text (rejected: the text is not structured and FR-012 requires it unaltered).

## R8 - Lineage view

- **Decision**: A disclosure-style `LineagePanel` per turn loads `getLineage` on first open and shows entries in
  sequence order with kind (worded for `submitted`, `claimed`, `checkpoint`, `lease-expired`, `deadline-reached`,
  `failed`, `scope-lost`, `exhausted`, `completed`; raw otherwise), attempt number, time, bounded details, and the
  advice's declared execution references; it refetches conditionally when the turn's polling reports a change and stops
  with the turn.
- **Rationale**: FR-015 and the clarified lineage refresh.

## R9 - Readable agent names and enabled agents

- **Decision**: `listAgents` (`listAvailableAgents`) supplies identifier, group, and domain; readable names come from the
  translation keys `agents.<agentId>` for the six catalog Specialist Agents, with the raw identifier as fallback. The
  add-participant control lists enabled agents not yet in the conversation and says when none remain.
- **Rationale**: FR-005 and the spec's assumption on readable names.

## R10 - Refusals, scope loss, and session

- **Decision**: A pure `triageMessage(outcome)` function maps each typed outcome to a translation key; refusals keep the
  draft (FR-016); `412` on add-participant reloads the conversation and asks the member to choose again; `404` on any
  read shows the not-found view and stops all polling (FR-018); `403` on submission hides the composer and shows the
  read-only note. Ended sessions use the existing session outcome middleware.
- **Rationale**: FR-016 to FR-018 and the existing `FailureAlert` and session flows.

## R11 - Online only and leaving

- **Decision**: The service worker's `classifyRequest` already leaves `/api/` unhandled; a unit test asserts it for every
  agent path and method, and a browser test asserts that Cache Storage, local and session storage, and IndexedDB hold
  no conversation content after a session. The triage views use the connectivity context to show the existing
  connection-required state and disable submission while offline. The existing `useLeaveGuard` hook
    (`src/uploads/useLeaveGuard.ts`) guards browser unload, and a React Router `useBlocker`
  confirmation guards in-app navigation while an unsent draft or an unknown-outcome submission exists.
- **Rationale**: FR-019, FR-019a, and [Offline Mode In A PWA](../../docs/architecture/client-applications.md#offline-mode-in-a-pwa)
  (agent submission and refresh stay connected-only).
  - **Alternatives considered**: Moving the hook to a shared folder (rejected: unrelated churn in the upload feature).

## R12 - Browser tests

- **Decision**: The `simulated` project gains `triage.spec.ts` (start, add participant, submit, pending, polling with
  scripted delays, success with advice and disclosure, each failure reason, history paging, lineage, superseded run,
  Viewer read-only, refusals, lost submission answer with key reuse, leave confirmation) and `triage-access.spec.ts`
  (roles, axe, keyboard, offline, no stored content), backed by agent routes in `tests/e2e/support/simulatedApi.ts` that
  serve `ETag`/`304`, `Retry-After`, scripted state changes, a one-shot network failure on submit, and recorded
  idempotency keys. The platform chain gains a `platform-triage` project (`platform/triage.spec.ts`), inserted before the
    final `platform` project (which then depends on it, so `--project platform` still runs the whole chain). It opens the
      match the upload flow finalized,
    checks the triage section with its empty list and the no-eligible-run message, and the not-found view for an unknown
    conversation.
- **Rationale**: The clarification: the platform test composition produces no `Completed` run, so the full flow is
  proven against the simulated API and the sibling's own tests prove the platform with its simulated agent workload;
  no test-only platform hook is added.
- **Alternatives considered**: An e2e-only seeded completed run (rejected by clarification).
