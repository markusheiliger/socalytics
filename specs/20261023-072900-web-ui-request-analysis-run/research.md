# Research: Web UI Request of a New Analysis Run

No `NEEDS CLARIFICATION` remains. Decisions below fix how the feature is built on the existing Web UI.

## R1. Operation and client

- **Decision**: Call `requestAnalysisRun` through the generated Kiota client from a new `analysisRunAdapter.requestRun(matchId, key)`,
  built with the existing `withIdempotencyKey` helper and no body and no `If-Match`, returning a model of the `202`
  answer (`runId`, `created`, `state`, `currentRunId`, `workflowId`, `workflowVersion`).
- **Rationale**: The operation, its answers, and its idempotency behavior are fixed by
  [the platform contract](../20261023-015644-explicit-analysis-rerun/contracts/request-analysis-run.md); adapters never
  generate keys, the caller owns them (existing rule in `kiota.ts`).
- **Alternatives**: Hand-written `fetch` (rejected: bypasses CSRF, session, and version middleware); a body with workflow
  choice (rejected: out of scope, the operation has no body).

## R2. Idempotency key lifecycle

- **Decision**: Extract the intent-key rule of `useQuestionDraft.ts` (create on first send, reuse while the outcome is
  unknown, drop on a definite answer) into `features/shared/intentKey.ts`; the request dialog opens one intent per
  confirmation: the key is created on the first send, reused for a repeat after a network failure or `503`, and dropped on
  any other answer and when the dialog closes, so a later confirmation always sends a new key.
- **Rationale**: One rule keeps idempotency consistent with triage (FR-007, FR-012); keys stay in React state or refs only
  (FR-015). A recorded refusal for a key is replayed by the platform, so a retry after the cause was fixed must use a new key.
- **Alternatives**: A key per page (rejected: replays a recorded refusal after the cause is fixed); `sessionStorage`
  (rejected: violates memory-only).

## R3. Mapping refusals

- **Decision**: Map by status and problem `code` in `requestAnalysisOutcome.ts` onto message keys:
  `409 no-finalized-recording-set`, `409 workflow-unresolvable` (with the category the problem reports: `not-configured` or
  `invalid-definition`, anything else generic), `409 season-archived` (existing `seasonArchived` kind), `409
  idempotency-key-reused`, `403`, `404`, `401` (existing session handling), `503`/network (temporary, repeat with the same
  key), and any other failure generically. Two small extensions of `failure.ts`: `parseProblem` keeps the problem's
  `category` extension (when the platform names it differently, the mapping follows the committed OpenAPI snapshot and
  falls back to the generic text), and the new codes get dedicated kinds instead of the generic `conflict`.
- **Rationale**: The existing `idempotency-key-reused` mapping to `programmingError` and the unspecific `conflict` kind
  cannot tell the member what happened (FR-010); the platform's code is the only input, so no rule moves to the client.
- **Alternatives**: Show the problem detail text (rejected: not localized, not guaranteed plain language); a decision in the
  client from lineage data (rejected: FR-010 forbids it).

## R4. Action gating and disabled state

- **Decision**: Show the action when `canEditTeamMatches(member, teamId)` (the cancel action's gate). Disable it with a text
  reason when the already loaded recording lineage has no recording-set version; otherwise enabled. While the lineage read
  is loading or failed the action is disabled without a reason claim. The platform's answer stays decisive.
- **Rationale**: FR-001, FR-002, FR-013; reuses the existing lineage query of the run list, so no extra read.
- **Alternatives**: Hide when not finalized (rejected: the member would not learn why).

## R5. Outcome display and re-read

- **Decision**: After `202`, set the outcome message, then invalidate and refetch the runs query (`RUNS_KEY`) and the
  lineage query once; the list renders currency and supersession exactly as reads report them. The outcome area is a
  `role="status"` region next to the action and links to the run (`/matches/{matchId}/analysis-runs/{runId}`) through the
  router `Link` the list already uses, and to the list entry through a fragment anchor on the run's list item when it is in
  the list. If the re-read fails, the outcome stays and the list shows its existing unavailable state.
- **Rationale**: FR-008, FR-009, FR-016; no manual reload, no client-side marking.
- **Alternatives**: Optimistic insert into the cache (rejected: the client would compute currency and supersession).

## R6. Refusals that change access

- **Decision**: After `403` or `404`, refetch the session member, the match, and the run list so the action disappears
  when access is gone; after `401` the existing session-ended handling takes over and the query cache is cleared by it.
- **Rationale**: FR-011 and spec User Story 2, scenarios 3 and 6; reuses the existing session and query handling.

## R7. Tests

- **Decision**: Unit tests for adapter, mapping, and key rule; the `simulated` project extends the fake platform with a
  scriptable `POST /matches/{id}/analysis-runs` (created, exists, each refusal, delayed answer, dropped connection) that
  records the received `Idempotency-Key`; the `platform` project drives the real API and provokes created, already
  exists, no finalized recording set, and unresolvable workflow (Registry default changed through the existing
  administration or test state), plus role gating, using the existing axe and keyboard helpers; browser storage and cache
  assertions follow the supersession and triage specs.
- **Rationale**: FR-018, SC-001 to SC-007.
