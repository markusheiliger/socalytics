# Quickstart: Validating the Request Action

Prerequisite: [Explicit Analysis Run Request](../20261023-015644-explicit-analysis-rerun/spec.md) is merged, so the
committed OpenAPI snapshot and the generated client contain `requestAnalysisRun`. Node.js 24, Kiota 1.36.0 on `PATH`, and
Playwright Chromium are installed as for any Web UI work (see the repository `README.md`).

## 1. Unit tests and build

```bash
cd src/clients/web
npm ci
npm run build
npm test
```

Expected: the adapter, the answer and refusal mapping of [ui-outcomes.md](contracts/ui-outcomes.md), and the intent-key
rule pass; the build type-checks against the generated client.

## 2. Simulated browser tests (no Docker)

```bash
cd src/clients/web
npm run test:e2e -- --project simulated run-request
```

Expected: role gating (Club Admin and Coach see the action; Viewer, Analyst, other team do not); disabled state without a
finalized set; confirmation text and "nothing sent before confirm"; pending state and single request; created, exists,
and exists-with-other-current outcomes with the re-read run list; each refusal with a distinct message and an unchanged
list; same key on repeat after a dropped connection or `503`, new key after any other answer; no data in browser
storage or caches; keyboard operation and axe checks.

## 3. Platform browser tests (Docker required)

```bash
cd src/clients/web
npm run test:e2e -- --project platform run-request
```

Expected against the real platform: a Coach requests analysis and sees the created run as current with the older run
superseded; repeating with the same recordings and workflow reports that the analysis already exists; a match without a
finalized recording set and a stamp without a resolvable default workflow show their refusals; role gating holds.

## 4. Manual check through the AppHost

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost` and sign in as a Coach of a team with a finalized match.
2. Open the match, choose "Request new analysis", read the confirmation, confirm, and watch the run list update.
3. Disconnect the network, confirm again: the member is told a connection is needed and nothing is queued.

## 5. Documentation

```bash
node .github/scripts/check-markdown.mjs
```
