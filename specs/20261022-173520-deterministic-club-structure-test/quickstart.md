# Quickstart: Deterministic Club Structure Browser Test

Run from `src/clients/web`. The platform projects start the AppHost in end-to-end mode and need Docker; `npm ci` and the Playwright Chromium install come first.

1. **Baseline and reproduction**: run the chain once with `npm run test:e2e -- --project platform-club-structure --trace on`, then repeat the test alone with `npm run test:e2e -- --project platform-club-structure --no-deps --trace on --repeat-each 20` (a `--repeat-each` that includes the dependencies would repeat `platform-session`, which fails on its second repeat). Season and team names must be derived per attempt (`run` computed inside the test or a per-repeat suffix from `test.info().repeatEachIndex`) before repeated runs count as evidence. Keep the traces of failing runs.
2. **Under load**: start CPU load on every core (for example one `yes > /dev/null` per core), run the repeated command of step 1 (`--no-deps`) with `--repeat-each 5`, then stop the load processes.
3. **After the platform projects it depends on**: `npm run test:e2e -- --project platform` at least 5 times (the whole `platform` chain, in which the test runs after `platform-session` and `platform-navigation`); the club structure result must pass in each.
4. **Failure probes** (each temporary, removed afterward): a team list that is not refreshed after a creation, a season that is not activated, and a save that does not persist the new name; each makes the test fail on its own assertions and not hang.
5. **Regression**: `npm run build`, `npm test`, and `npm run test:e2e -- --project simulated`; `node .github/scripts/check-markdown.mjs` for this folder.

Expected outcome: all runs of steps 1 to 3 pass without a retry, the probes of step 4 fail the test, and the code comment and commit message name the evidenced cause ([research.md](research.md) lists the candidates). Passing runs are development evidence only.
