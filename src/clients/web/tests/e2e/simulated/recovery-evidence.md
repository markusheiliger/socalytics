# Upload Recovery Test Evidence

## T001

Tooling confirmation in `src/clients/web` for the commands of the deterministic upload recovery test quickstart.

| Item | Version or outcome |
| --- | --- |
| Node.js (`node --version`) | v24.21.0 (`engines.node` is `>=24 <25`) |
| Playwright (`npx playwright --version`) | 1.63.0 |
| Chromium (`npx playwright install chromium`) | installed, no errors |

| Command | Outcome |
| --- | --- |
| `npm ci` | succeeded (only an `esbuild` install-script advisory and an `npm audit` hint) |
| `npx playwright install chromium` | succeeded |
| `npm run build` | succeeded (only the chunk-size warning) |
| `npx playwright test --project=simulated tests/e2e/simulated/recovery.spec.ts --workers=1` | 6 passed (36.9s), one run of each test, no retries |
