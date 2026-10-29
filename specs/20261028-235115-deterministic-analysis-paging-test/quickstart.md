# Quickstart: Validating the Deterministic Paging Test

Prerequisites: Node.js 24 and Playwright 1.63.0 Chromium; no Docker needed. From `src/clients/web` after `npm ci`.

1. Run the test alone, at least 20 times in a row:
   `npx playwright test tests/e2e/simulated/analysis-review-access.spec.ts --project simulated -g "every control is reachable by keyboard" --repeat-each 20 --workers 1`.
   Expected: all pass.
2. Run the whole file at least 5 times under CPU load and once with the filtered facts request delayed. Expected: all pass.
   - **Load**: one busy loop per CPU core, started before the first run and kept running for the whole run.
   - **Delay**: a fixed 5000 ms on the request whose URL contains `fromMs=10000` (a temporary `page.route` on `factsRequest`, removed afterward).
3. Before the fix, reproduce the failure the same way (step 2) and keep the trace; record the evidence in the code comment next to the fix and in the task report ([research.md](research.md) holds the analysis made before).
4. Probes, each removed afterward: keep "Next page" disabled for the filtered selection; move the focus away from the control. Expected: the test fails on an unchanged assertion.
5. Run the rest of the browser suite: `npm run test:e2e -- --project simulated`. Expected: unchanged results.
6. Validate Markdown: `node .github/scripts/check-markdown.mjs`.

Passing runs are development evidence only.
