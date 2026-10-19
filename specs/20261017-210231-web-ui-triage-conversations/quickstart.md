# Quickstart: Web UI Match Triage Conversations

Validation guide for the triage views. Routes and roles: [contracts/routes-and-roles.md](contracts/routes-and-roles.md);
adapter: [contracts/agent-conversation-adapter.md](contracts/agent-conversation-adapter.md); messages:
[contracts/turn-states.md](contracts/turn-states.md).

## Prerequisites

- The platform side of [Agent Orchestration Direct Invocations](../archive/20261017-204332-agent-orchestration-invocations/spec.md)
  is merged, so the committed OpenAPI snapshot contains the agent operations.
- Node.js 24, Kiota 1.36.0 on `PATH`, Playwright 1.63.0 Chromium; Docker and the .NET SDK for the platform project.

## Build and unit tests

From `src/clients/web`:

```powershell
npm ci
npm run build      # regenerates the Kiota client from the snapshot, type-checks, builds
npm test           # Vitest: adapter, polling arithmetic, draft keys, presentation, components, i18n keys, service worker
```

Expected: the adapter maps every operation and refusal, `nextPollDelay` grows by 1.5 up to 15 seconds and resets on
change, the draft reuses its key only for an unchanged intent after an unknown outcome, and `classifyRequest` leaves
every agent path unhandled.

## Browser tests

```powershell
npm run test:e2e -- --project simulated   # triage.spec.ts and triage-access.spec.ts
npm run test:e2e -- --project platform    # whole chain, including platform-triage
```

Expected in `simulated`:

1. A Coach starts a conversation for the newest eligible run, adds the tactics specialist, asks a question, sees the
   pending turn at once and the advice with attribution and AI disclosure without a reload, within 30 seconds.
2. Each failure reason, each refusal, the superseded-run notice, history paging, and the lineage panel show their
   messages; a lost submission answer followed by "Send again" leaves one turn and one idempotency key.
3. A Viewer reads their own conversation without controls; a non-owner or unknown conversation shows not found.
4. Polling stops while hidden, offline, and after the turn ended; offline shows the connection-required state.
5. Axe reports no WCAG 2.2 AA violations, every flow works by keyboard, and no conversation content is found in Cache
   Storage, local or session storage, or IndexedDB.

Expected in `platform-triage`: the uploaded match shows the triage section with its empty list and the no-eligible-run
message, and an unknown conversation address shows the not-found view.
