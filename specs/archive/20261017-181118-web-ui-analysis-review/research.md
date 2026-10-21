# Research: Web UI Connected Analysis Review

Decisions for [plan.md](plan.md). Each entry states the decision, its
rationale, and the alternatives considered.

## R1 — Route and address

- **Decision**: `/matches/:matchId/analysis-runs/:runId`, with the optional
  search parameters `stream` (node key), `fromMs`, and `toMs` (non-negative
  integer milliseconds of match time). The page position is not in the
  address.
- **Rationale**: The spec requires an addressable review whose selection
  survives a reload or a shared link (Clarification 1, FR-001). Nesting under
  the match reuses the match context and its access rule, and lets the view
  detect a run that belongs to another match (FR-003). Milliseconds are the
  platform's unit and avoid lossy round trips; the form shows them as a match
  clock.
- **Alternatives**: `/analysis-runs/:runId` without the match (loses the
  match check and breadcrumbs); match-clock text in the address (needs a
  second parser and is lossy); continuation tokens in the address (opaque,
  selection-bound, and forward-only, so a shared link could not reproduce a
  page reliably).

## R2 — Access and refusals

- **Decision**: The route view loads the match through `useMatchContext` and
  shows `NotPermittedPage` when the member has no role on the team, as
  `MatchPage` does. The run read is authoritative: a run whose `matchId`
  differs from the route, or a `404` from `getAnalysisRun`, shows
  `NotFoundPage`. The analysis adapters translate `404` problems themselves
  into typed outcomes (`runNotFound`, `capabilityNotInRun` for the code
  `analysis-capability-not-in-run`) and a `400` whose field errors name
  `continuationToken` into `continuationRejected`; the shared `mapProblem`,
  which maps `404` to `incompatible` and continuation field errors to
  `programmingError`, stays unchanged.
- **Rationale**: FR-002 and FR-003 require not-found or not-permitted views
  without disclosure; changing the global mapping would alter every other
  view's behavior.
- **Alternatives**: A global `notFound` failure kind (wider change, touches
  every adapter's tests); trusting only the client-side role check (the
  platform must stay authoritative).

## R3 — Platform reads and generated client

- **Decision**: Use `getAnalysisRun`, `listAnalysisRunFactStreams`, and
  `listAnalysisRunFacts` through the Kiota client generated from the committed
  OpenAPI snapshot. No new platform operation.
- **Rationale**: The run read supplies run and node status with job counts;
  the batch sibling's stream listing supplies per-node counts, provenance, and
  coverage counts; its fact read supplies facts, the stream provenance, and
  coverage windows, always with the node key so a capability that appears in
  several nodes is never ambiguous (FR-013). Every field the spec lists is
  covered, so the "small missing read" exception of the feature description
  does not apply.
- **Alternatives**: A combined review read on the platform (duplicates the
  sibling's operations).
- **Dependency**: the fact operations exist in the snapshot only after the
  batch sibling merges; this feature is implemented after it.

## R4 — Payload values from the generated client

- **Decision**: The adapter normalizes each fact's payload into a plain
  `JsonValue` (object, array, string, number, boolean, or null), whatever form
  Kiota produces for the schema-less payload object (an untyped node or a
  model with `additionalData`), and treats any other shape as `incompatible`.
  Unit tests cover both forms.
- **Rationale**: The payload is capability-owned and returned exactly as
  accepted; the review must show it unchanged (FR-008) and must not depend on
  Kiota's internal representation in components.
- **Alternatives**: Bypassing the generated client for the fact read
  (violates FR-017).

## R5 — Paging

- **Decision**: `useFactPages` keeps, per selection (run, node key,
  capability id and version, schema version, range), a stack of continuation
  tokens in component state: page 1 has none, page *n* + 1 uses the token
  returned by page *n*. "Next" pushes, "Previous" pops; the stack is discarded
  when the selection changes, on reload, and when the platform rejects a
  continuation (with a message). The platform's default page size is used.
- **Rationale**: Clarification 2 and FR-009; the platform's continuation is
  forward-only, so previous pages are reproduced from remembered tokens.
- **Alternatives**: "Load more" appending (unbounded DOM, harder focus
  management); page numbers (the platform offers no offsets).

## R6 — Refresh while a run is in progress

- **Decision**: The run query and the stream-list query use
  `refetchInterval` of 10 seconds (the existing `RUN_REFRESH_MS`) while the
  run state is `Requested` or `Running`, `refetchIntervalInBackground: false`,
  and no window-focus refetch; once the run is final they stop. The fact-page
  query does not refetch by itself; when the refreshed stream summary's fact
  count or coverage counts differ from those the open page was loaded with,
  the browser shows a "new results are available" notice with a reload action
  that restarts at the first page. Changes are announced through a polite live
  region.
- **Rationale**: FR-006 and SC-004 (visible within 15 seconds) without moving
  the member's reading position (User Story 3, scenario 5).
- **Alternatives**: Server push (not offered by the platform); refetching the
  open page (would reorder content under the member).

## R7 — Match clock

- **Decision**: `formatMatchClock(ms)` renders `m:ss` with unbounded minutes
  (for example `105:30`) and appends `.SSS` milliseconds only when they are not
  zero; ranges render as `start – end` with an accessible label "from … to …
  (end excluded)". `parseMatchClock(text)` accepts `m:ss` and `m:ss.SSS`
  (seconds below 60) and rejects anything else; the form requires the end
  after the start, and an empty field means unbounded on that side.
- **Rationale**: Readable times for coaches, exact values for instantaneous
  facts, and extra time beyond 90 minutes (edge case).
- **Alternatives**: Locale time formats (not match time); seconds only (loses
  short facts).

## R8 — Payload summary and full payload

- **Decision**: `summarizePayload(value)` lists at most 6 top-level fields of
  an object in their returned order as `name: value`, with scalars cut to 60
  characters, nested objects as "{n fields}", and arrays as "[n items]"; more
  fields are counted ("+3 more"). A disclosure shows the full payload as
  `JSON.stringify(value, null, 2)` inside a `<pre>` text node. Kinds and
  payload text are always React text children.
- **Rationale**: FR-008 and FR-020; bounded rendering for large or deeply
  nested payloads (edge case) without interpreting capability semantics.
- **Alternatives**: A JSON tree component (new dependency); per-capability
  renderers (capability semantics are out of scope).

## R9 — Provenance digests

- **Decision**: Digests display as the algorithm prefix plus the first 12
  hexadecimal characters and an ellipsis; the full value is in a visually
  available, selectable `<code>` element revealed by a disclosure, with a copy
  button that uses `navigator.clipboard.writeText` and announces success or
  failure; without clipboard access the text stays selectable.
- **Rationale**: FR-011; recognizable values without overwhelming the layout.
- **Alternatives**: Full digests inline (hard to read); tooltip only (not
  keyboard or touch friendly).

## R10 — Explicit review states

- **Decision**: A pure function `deriveFactViewState` decides, in this order:
  `connectionRequired` (offline), `notFound`, `notPermitted`,
  `capabilityNotInRun` (platform code, or a `stream` node key absent from the
  listing), `invalidRange`, `error`, `loading`, then with data: `facts` (with
  an `incomplete` flag when the run is unfinished or coverage has a window
  that is not `accepted`), and without facts `noFactsInRange` (range set),
  `inProgress` (run requested or running), `incomplete` (coverage has a
  non-accepted window), or `completeWithoutFacts`. Each state has its own
  translation key; coverage is shown in every data state. See
  [contracts/review-states.md](contracts/review-states.md).
- **Rationale**: FR-014 to FR-016 and SC-003 require distinct, testable
  messages; a pure function is exhaustively unit-testable.
- **Alternatives**: State spread across components (untestable precedence).

## R11 — Online only and the service worker

- **Decision**: No change to the service worker: `classifyRequest` already
  leaves `/api/` unhandled, so analysis responses always go to the network.
  A unit test asserts the three analysis paths are unhandled, and the
  simulated browser test asserts that no `/api/` entry is in Cache Storage and
  no analysis data in `localStorage`, `sessionStorage`, or IndexedDB after a
  review. Offline, the existing connectivity state shows the
  connection-required message and the review renders no previously loaded
  analysis data.
- **Rationale**: FR-018 and [Offline Mode](../../../docs/architecture/client-applications.md#offline-mode).
- **Alternatives**: Caching analysis responses for faster reloads (forbidden
  before the offline replica exists).

## R12 — Value presentation

- **Decision**: Confidence shows as a whole percentage through
  `Intl.NumberFormat('en', { style: 'percent' })`; run, node, and coverage
  states, tiers, scopes, and output types use translation keys looked up
  case-insensitively (the existing run API returns `Running`, the sibling's
  contract examples show lower case) with the raw value as the fallback
  (FR-016); timestamps reuse `formatKickoff`.
- **Rationale**: Consistency with the match view and resilience to new
  values.

## R13 — Browser tests

- **Decision**: The `simulated` project gets `analysis-review.spec.ts` (run
  status, fact browsing, explicit states, refresh) and
  `analysis-review-access.spec.ts` (roles, axe, keyboard, offline, and storage
  checks): the simulated API serves `GET /analysis/runs/{id}`, `…/fact-streams`, and
  `…/facts` from seeded state (run states, a stream with at least three pages,
  mixed coverage, provenance with and without a model digest, a capability
  absent from the run, rejected continuations), and together the two specs cover roles,
  every explicit state, paging, ranges, refresh (the seeded run changes state
  between polls), axe, keyboard, offline, and storage checks. The platform
  chain gets a `platform-analysis` project (`platform/analysis-review.spec.ts`)
  that depends on `platform-upload` and precedes `platform-recovery`; it opens
  the review of the run created by the upload flow, checks run and node status,
  the stream list with zero facts, the in-progress or incomplete message, and
  the not-found view for an unknown run.
- **Rationale**: The platform project has no Manager that executes jobs, so
  accepted facts are not available there; fact browsing is proven against the
  simulated API, and the sibling's own end-to-end tests prove the platform
  reads with facts. FR-022 asks for one browser test against the running
  platform that opens a run's review from a match.
- **Alternatives**: Executing jobs in the Web UI platform project with a
  scripted Manager that uploads results (large new test harness duplicating
  the platform's end-to-end tests).

## R14 — The echoed range of a fact page (refined)

- **Decision**: The adapter maps the fact page's `range` bounds as optional:
  `null` (the platform's answer for a bound the request did not set) becomes
  an absent bound, and the simulated API echoes the same (`null` for an unset
  bound), not an invented effective range.
- **Rationale**: `ListRunFactsHandler` returns the parsed `fromMs` and `toMs`
  unchanged (`long?`), and `FactRangeDto` declares both nullable, so the first
  page of a stream read without a range carries `{ "fromMs": null,
  "toMs": null }`. The adapter and the simulated API had assumed finite numbers
  ("the effective range"), so the adapter reported `incompatible` for every
  real unranged read and the review showed the error state instead of
  `inProgress` or `incomplete`. The simulated API hid this, and the
  `platform-analysis` spec of T023 is the first test to open a stream against
  the running platform (T023 failed twice with the in-progress or incomplete
  message missing).
- **Alternatives**: Treating a `null` bound as zero or as the coverage end in
  the adapter (invents values the platform did not report); changing the
  platform to echo an effective range (outside this feature; the fact read is
  merged and tested by its own feature).
