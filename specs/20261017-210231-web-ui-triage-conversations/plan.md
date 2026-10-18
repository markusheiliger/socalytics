# Implementation Plan: Web UI Match Triage Conversations

**Branch**: `20261017-210231-web-ui-triage-conversations` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261017-210231-web-ui-triage-conversations/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Add connected match triage conversations to the Web UI at `src/clients/web/` on top of the member operations of
[Agent Orchestration Direct Invocations](../20261017-204332-agent-orchestration-invocations/spec.md).

- **Entry**: the match view gains a triage section with the member's own conversations and, for Club Admins and Coaches
  of the team with a `Completed` or `PartiallyCompleted` run, a start dialog; conversations open at
  `/matches/:matchId/triage/:conversationId`, which links to the run review route
  `/matches/:matchId/analysis-runs/:runId`.
- **Platform reads and writes only**: the nine member operations of the sibling (no new platform operation) and the
  existing match and run reads; the Kiota client is regenerated from the snapshot by the existing `npm run build`.
- **Adapter**: a new `agentConversationAdapter` with conditional reads (a response handler turns `304` into
  `unchanged` and exposes `ETag` and `Retry-After`) and triage-specific refusal outcomes, leaving the shared problem
  mapping unchanged.
- **Behavior**: idempotency keys owned by an in-memory question draft and reused only for an unchanged intent after an
  unknown outcome; per-turn status polling that starts at the platform's interval, grows by 1.5 to 15 seconds while
  unchanged, pauses while hidden or offline, and stops at a terminal state; history paging with "Show more"; advice as
  text with attribution, AI disclosure, and an explicit "no evidence references" note; on-demand lineage panels.
- **Online only**: TanStack Query keeps data in memory; the service worker already leaves `/api/` unhandled; drafts and
  keys never leave memory; a leave confirmation protects unsent input.
- **Evidence**: Vitest unit tests, Playwright `simulated` specs against agent routes of the simulated API, and a
  chained `platform-triage` project against the AppHost `e2e` mode.

Design rationale: [research.md](research.md). Client models: [data-model.md](data-model.md). Contracts:
[contracts/](contracts/). Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: TypeScript 5.9 (strict) on Node.js 24, as pinned by `src/clients/web/package.json`.

**Primary Dependencies**: the Web UI's existing pinned dependencies (React 19, React Router 7, TanStack Query 5, MUI 7,
i18next and react-i18next, `@microsoft/kiota-bundle` and `@microsoft/kiota-abstractions`, Vite 7, Vitest 4, Testing
Library, `@playwright/test` 1.63.0, `@axe-core/playwright`). No new dependency. Browser APIs: `crypto.randomUUID`,
Page Visibility, `beforeunload`.

**Storage**: none in the browser. Conversation data lives only in the in-memory TanStack Query cache and component state;
no persister, IndexedDB, local or session storage, and no service-worker caching (FR-019).

**Testing**:

- `npm test` (Vitest): adapter mapping, conditional reads, and refusal outcomes; `nextPollDelay`; `useQuestionDraft`
  key rules; `useInvocationPolling` with fake timers, visibility, and connectivity; `turnPresentation` and
  `triageMessage`; components with Testing Library; i18n keys; service-worker classification of the agent paths.
- `npm run test:e2e -- --project simulated`: `triage.spec.ts` (flow, states, refusals, paging, lineage, key reuse,
  leave confirmation) and `triage-access.spec.ts` (roles, axe, keyboard, offline, no stored content).
- `npm run test:e2e` platform chain: new `platform-triage` project (`platform/triage.spec.ts`) against the AppHost
  `e2e` mode.

**Target Platform**: Chromium-based browsers (automated: Chromium on Linux), as for the rest of the Web UI.

**Project Type**: web application (single-page application) consuming the platform API.

**Performance Goals**: the pending turn renders within 1 second of the `202` (SC-001); advice visible within 30 seconds
with the platform's 2-second development interval and a 15-second polling cap (SC-002).

**Constraints**:

- Only the sibling's member operations and the existing match and run reads (FR-020); no streaming, editing, deletion,
  sharing, Coach Agents, consultations, clips, or offline use (FR-023).
- Platform text rendered as text only; no `dangerouslySetInnerHTML`; the Content Security Policy and Trusted Types
  baseline unchanged (FR-022).
- English through translation keys; keyboard operable; live-region announcements; WCAG 2.2 AA rule checks (FR-021).
- The shared `mapProblem` and `FailureAlert` stay unchanged; triage outcomes are derived in the new adapter.

**Scale/Scope**: one new route, one match-view section, one adapter and model file, three hooks, about eight components,
one translation namespace, two simulated browser specs, and one platform browser spec; tens of conversations per match
and up to the platform's limit of unfinished questions per member.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the agent conversations of [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) and [Connected Agent Conversations](../../docs/architecture/client-applications.md#connected-agent-conversations), the Web UI side of [Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol), and the advice disclosure of [Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance); stays online only per [Offline Mode](../../docs/architecture/client-applications.md#offline-mode). Design updates are applied now (see Architecture Updates). |
| II. Source-area ownership | Pass | Changes only under `src/clients/web/`; no new `src/` child; the platform is untouched. |
| III. API-first control plane | Pass | The Web UI uses only `/api/v1` member operations through the generated client behind adapters; every rule (roles, limits, eligibility, privacy) stays with the platform; the Web UI never calls Hermes or the transport. |
| IV. Evidence over claims | Pass | Unit, simulated browser, and platform browser tests (FR-024); the full ask-and-answer flow is proven against the simulated API, and nothing claims production readiness. |
| V. Focused, minimal changes | Pass | No new dependency, platform operation, project, or test-only platform hook; reuses the match context, connectivity, session, leave guard, failure, and i18n patterns. |
| Technology and tooling | Pass | Needs Node.js 24, Kiota 1.36.0, Playwright 1.63.0 Chromium, the .NET SDK, and Docker for the platform project, all provided by `.github/actions/environment-setup` and `environment-verify` through the merged [Web UI Verification Environment](../archive/20261012-145505-web-ui-environment/spec.md); no environment spec is needed. |

Post-design re-check: Pass (no violations; Complexity Tracking is empty).

## Architecture Updates

### Narratives

- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): agent conversations for coaches
  in the connected Web UI.
- [Client Applications → Connected Agent Conversations](../../docs/architecture/client-applications.md#connected-agent-conversations):
  submission with an idempotency key, `202`, conditional polling, private reads, connected-only rules; the Web UI's
  concrete behavior (key reuse after a lost answer, backoff, pausing while hidden or offline).
- [Intelligence and Agents → Client, Transport, And Runtime Protocol](../../docs/architecture/intelligence-and-agents.md#client-transport-and-runtime-protocol)
  and [Post-Analysis Match Triage](../../docs/architecture/intelligence-and-agents.md#post-analysis-match-triage): the
  Web UI side of the protocol and of the run-bound triage conversation; no design change.
- [Intelligence Agent Catalog → Prompt And Advice Governance](../../docs/architecture/intelligence-agent-catalog.md#prompt-and-advice-governance):
  the AI disclosure and attribution of Specialist Agent advice in the Web UI; no design change.
- [Platform Implementation Profile → Agent Runtime](../../docs/architecture/platform-implementation.md#agent-runtime),
  [Client Technology](../../docs/architecture/platform-implementation.md#client-technology), and the Architecture
  Reassessment and Acceptance Evidence lists: status of the Web UI agent workflows.
- [Client Applications → Offline Mode In A PWA](../../docs/architecture/client-applications.md#offline-mode-in-a-pwa):
  unchanged rule (agent submission and refresh stay connected-only) that the feature follows.

### Design updates (applied now, worded as decided but not implemented)

- `client-applications.md`, Web UI: the bullet "team and match review, analytics, and agent conversations for coaches"
  notes that the analysis review and the match triage conversations are decided (see below), while still not
  implemented.
- `client-applications.md`, Web UI: a paragraph that the connected match triage conversations are decided as follows
  and not yet implemented: from a match with a `Completed` or `PartiallyCompleted` run, Club Admins and Coaches of the
  owning team start a conversation for a chosen run, add enabled specialists, and ask one of them; members list and
  reopen their own conversations from the match view, see the bound run with a link to its review and whether it is
  still current, and read the history, advice with agent attribution and AI disclosure, and each turn's lineage; Viewers
  read their own conversations without controls; the views are online only and keep conversation content, drafts, and
  idempotency keys in memory. The implementation turns it into present tense.
- `client-applications.md`, Connected Agent Conversations: a sentence that the Web UI reuses a submission's idempotency
  key when the answer was lost and the question is unchanged, and checks status conditionally starting at the
  platform's retry guidance with backoff up to a cap while unchanged, pausing while hidden or offline (decided, not yet
  implemented). The implementation turns it into present tense.

### Status statements at merge

- `client-applications.md`, Web UI: "team and match review, analytics, and agent conversations for coaches (analysis
  review and agent conversations are not implemented yet; the analysis review and the match triage conversations are
  decided, see below)" → agent conversations for coaches are implemented as a
  connected workflow for direct Specialist Agent triage; correct the parenthesis for whatever the analysis review's
  status is at merge, and keep Coach Agents and consultations as not implemented.
- `platform-implementation.md`, Agent Runtime: "MCP tools, prompt/profile resolution, model execution, and Web UI agent
  workflows remain future implementation work." → the Web UI's direct triage conversations exist as development
  evidence; MCP tools, prompt/profile resolution, model execution, and Coach Agent and consultation workflows remain
  future work.
- `platform-implementation.md`, Architecture Reassessment: "the other Web UI client workflows (analysis review and agent
  conversations) … remain unsupported by implementation evidence" → agent conversations leave the list, and the
  triage conversations join "the online-only Web UI foundation exists as development evidence, including …".
- `platform-implementation.md`, Acceptance Evidence: the Web UI browser-test bullet gains the triage conversations
  (`simulated` project and the `platform-triage` project).
- `intelligence-agent-catalog.md`, introduction: "… Hermes orchestration runtime, and user experiences are likewise
  design inputs rather than deployed behavior." → the Web UI's direct specialist triage conversation is development
  evidence; the remaining items stay design inputs.
- `client-applications.md`, Connected Agent Conversations: the sentence added now loses "decided and not yet
  implemented"; re-check that "A valid direct Coach Agent or Specialist Agent submission …" still makes clear that only
  Specialist Agent submissions exist.
- `client-applications.md`, Web UI: the triage paragraph added now loses "decided as follows and not yet implemented".
- Any other statement in `docs/architecture/` that presents the Web UI's agent conversations as planned, future, or not
  implemented (for example in `README.md` or `overview.md`).

The documentation task MUST make these narratives and every status statement this feature affects true for the merged
state, re-reading `docs/architecture/` (and `AGENTS.md` "Current State", `src/clients/README.md`, and the root
`README.md`) at implementation time; the rewrites above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261017-210231-web-ui-triage-conversations/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── routes-and-roles.md
│   ├── agent-conversation-adapter.md
│   └── turn-states.md
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
src/clients/web/
├── src/
│   ├── router.tsx                                   # route /matches/:matchId/triage/:conversationId
│   ├── i18n/
│   │   ├── index.ts                                 # registers the triage namespace
│   │   └── en/triage.json                           # new namespace
│   ├── platform/
│   │   ├── models/agents.ts                         # client models (data-model.md)
│   │   └── adapters/agentConversationAdapter.ts     # operations, conditional reads, refusal outcomes
│   └── features/
│       ├── matches/MatchPage.tsx                    # renders TriageSection after the run list
│       └── triage/
│           ├── TriageSection.tsx                    # own conversations, start action
│           ├── StartConversationDialog.tsx          # eligible runs, creation intent and key
│           ├── ConversationPage.tsx                 # route view: access, run, participants, history, composer
│           ├── ParticipantsPanel.tsx                # participants and add control
│           ├── TurnList.tsx                         # history pages, pending turns, show more
│           ├── AdviceCard.tsx                       # attribution, disclosure, text, evidence note
│           ├── LineagePanel.tsx                     # on-demand lineage
│           ├── QuestionComposer.tsx                 # draft, agent choice, send, send again
│           ├── triageKeys.ts                        # query keys
│           ├── pollDelay.ts                         # nextPollDelay
│           ├── turnPresentation.ts                  # turnPresentation, triageMessage
│           ├── useQuestionDraft.ts                  # draft and idempotency key
│           └── useInvocationPolling.ts              # conditional status polling
└── tests/
    ├── unit/
    │   ├── platform/adapters/agentConversationAdapter.test.ts
    │   ├── features/triage/*.test.ts(x)
    │   └── serviceWorker/shellWorker.test.ts         # agent paths stay unhandled
    └── e2e/
        ├── support/simulatedApi.ts                   # agent routes, ETag/304, scripted changes, network fault
        ├── simulated/triage.spec.ts
        ├── simulated/triage-access.spec.ts
        └── platform/triage.spec.ts
src/clients/web/playwright.config.ts                  # chained platform-triage project
```

**Structure Decision**: the triage views form a new feature folder `src/features/triage/`, following the Web UI's
feature, adapter, and model layering; the match view only gains the section, and the run review view is linked to
without change.

## Complexity Tracking

No violations.
